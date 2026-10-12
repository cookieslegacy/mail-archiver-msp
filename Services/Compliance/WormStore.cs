using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Compliance
{
    /// <summary>
    /// Write-once storage for originals and anchors. With S3 Object Lock in compliance mode an
    /// object cannot be deleted or overwritten before its retention date by anyone, including
    /// the administrators of this application and of the bucket.
    /// </summary>
    public interface IWormStore
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Stores the object under retention. If an object with the key exists already (the keys
        /// are content addressed), it is reused and its retention extended when necessary.
        /// </summary>
        Task<WormPutResult> PutAsync(string key, byte[] content, DateTime? retainUntilUtc, bool legalHold,
            CancellationToken cancellationToken = default);

        Task<byte[]> GetAsync(string key, string? versionId, CancellationToken cancellationToken = default);

        Task ExtendRetentionAsync(string key, string? versionId, DateTime retainUntilUtc, CancellationToken cancellationToken = default);

        Task SetLegalHoldAsync(string key, string? versionId, bool on, CancellationToken cancellationToken = default);

        /// <summary>Deletes an object version whose retention has expired.</summary>
        Task DeleteAsync(string key, string? versionId, CancellationToken cancellationToken = default);

        Task<WormHealth> CheckAsync(CancellationToken cancellationToken = default);
    }

    public record WormPutResult(string Key, string? VersionId, bool Reused);

    public record WormHealth(bool Ok, string Message);

    /// <summary>Used when originals are kept in the database only.</summary>
    public sealed class DisabledWormStore : IWormStore
    {
        public static readonly DisabledWormStore Instance = new();

        public bool IsEnabled => false;

        public Task<WormPutResult> PutAsync(string key, byte[] content, DateTime? retainUntilUtc, bool legalHold,
            CancellationToken cancellationToken = default) => throw Disabled();
        public Task<byte[]> GetAsync(string key, string? versionId, CancellationToken cancellationToken = default) => throw Disabled();
        public Task ExtendRetentionAsync(string key, string? versionId, DateTime retainUntilUtc, CancellationToken cancellationToken = default) => throw Disabled();
        public Task SetLegalHoldAsync(string key, string? versionId, bool on, CancellationToken cancellationToken = default) => throw Disabled();
        public Task DeleteAsync(string key, string? versionId, CancellationToken cancellationToken = default) => throw Disabled();
        public Task<WormHealth> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WormHealth(false, "WORM storage is not configured (ArchiveStorage:Mode = Database)."));

        private static InvalidOperationException Disabled() =>
            new("WORM storage is not configured (ArchiveStorage:Mode = Database).");
    }

    public sealed class S3WormStore : IWormStore, IDisposable
    {
        // S3 accepts retention dates far in the future, but not "infinity"; 100 years is the
        // longest retention this application can configure.
        private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(366 * 101);

        private readonly S3WormOptions _options;
        private readonly AmazonS3Client _client;
        private readonly ILogger<S3WormStore> _logger;

        public S3WormStore(IOptions<ArchiveStorageOptions> options, ILogger<S3WormStore> logger)
        {
            _options = options.Value.S3;
            _logger = logger;

            var config = new AmazonS3Config
            {
                ForcePathStyle = _options.ForcePathStyle,
                AuthenticationRegion = _options.Region
            };
            if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
                config.ServiceURL = _options.ServiceUrl;
            else
                config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_options.Region);

            _client = new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), config);
        }

        public bool IsEnabled => true;

        private string FullKey(string key) =>
            string.IsNullOrEmpty(_options.Prefix) ? key : _options.Prefix.TrimEnd('/') + "/" + key;

        private ObjectLockRetentionMode Mode =>
            string.Equals(_options.LockMode, "GOVERNANCE", StringComparison.OrdinalIgnoreCase)
                ? ObjectLockRetentionMode.Governance
                : ObjectLockRetentionMode.Compliance;

        private static DateTime? Clamp(DateTime? retainUntilUtc)
        {
            if (retainUntilUtc == null)
                return null;
            var max = DateTime.UtcNow + MaxRetention;
            var value = retainUntilUtc.Value.ToUniversalTime();
            // A retention date in the past (no retention obligation) means no lock at all.
            if (value <= DateTime.UtcNow.AddMinutes(5))
                return null;
            return value > max ? max : value;
        }

        public async Task<WormPutResult> PutAsync(string key, byte[] content, DateTime? retainUntilUtc, bool legalHold,
            CancellationToken cancellationToken = default)
        {
            var fullKey = FullKey(key);
            var retain = Clamp(retainUntilUtc);

            var existing = await TryGetMetadataAsync(fullKey, cancellationToken);
            if (existing != null)
            {
                if (existing.ContentLength == content.LongLength)
                {
                    if (retain.HasValue && (existing.ObjectLockRetainUntilDate == null
                                            || existing.ObjectLockRetainUntilDate.Value.ToUniversalTime() < retain.Value))
                    {
                        await ExtendRetentionAsync(key, existing.VersionId, retain.Value, cancellationToken);
                    }
                    if (legalHold)
                        await SetLegalHoldAsync(key, existing.VersionId, true, cancellationToken);
                    return new WormPutResult(key, existing.VersionId, Reused: true);
                }
                _logger.LogWarning("WORM object {Key} exists with a different size; storing a new version", fullKey);
            }

            using var stream = new MemoryStream(content, writable: false);
            var request = new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = fullKey,
                InputStream = stream,
                ContentType = key.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? "message/rfc822" : "application/json",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ObjectLockLegalHoldStatus = legalHold ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off
            };
            if (retain.HasValue)
            {
                request.ObjectLockMode = Mode == ObjectLockRetentionMode.Governance ? ObjectLockMode.Governance : ObjectLockMode.Compliance;
                request.ObjectLockRetainUntilDate = retain.Value;
            }

            var response = await _client.PutObjectAsync(request, cancellationToken);
            return new WormPutResult(key, response.VersionId, Reused: false);
        }

        private async Task<GetObjectMetadataResponse?> TryGetMetadataAsync(string fullKey, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = _options.Bucket,
                    Key = fullKey
                }, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<byte[]> GetAsync(string key, string? versionId, CancellationToken cancellationToken = default)
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.Bucket,
                Key = FullKey(key),
                VersionId = string.IsNullOrEmpty(versionId) ? null : versionId
            }, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        public async Task ExtendRetentionAsync(string key, string? versionId, DateTime retainUntilUtc,
            CancellationToken cancellationToken = default)
        {
            var retain = Clamp(retainUntilUtc);
            if (retain == null)
                return;
            await _client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = _options.Bucket,
                Key = FullKey(key),
                VersionId = string.IsNullOrEmpty(versionId) ? null : versionId,
                Retention = new ObjectLockRetention { Mode = Mode, RetainUntilDate = retain.Value }
            }, cancellationToken);
        }

        public async Task SetLegalHoldAsync(string key, string? versionId, bool on, CancellationToken cancellationToken = default)
        {
            await _client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = _options.Bucket,
                Key = FullKey(key),
                VersionId = string.IsNullOrEmpty(versionId) ? null : versionId,
                LegalHold = new ObjectLockLegalHold { Status = on ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off }
            }, cancellationToken);
        }

        public async Task DeleteAsync(string key, string? versionId, CancellationToken cancellationToken = default)
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _options.Bucket,
                Key = FullKey(key),
                VersionId = string.IsNullOrEmpty(versionId) ? null : versionId
            }, cancellationToken);
        }

        public async Task<WormHealth> CheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest
                {
                    BucketName = _options.Bucket
                }, cancellationToken);
                var enabled = response.ObjectLockConfiguration?.ObjectLockEnabled == ObjectLockEnabled.Enabled;
                return enabled
                    ? new WormHealth(true, $"Bucket '{_options.Bucket}' reachable, Object Lock enabled, mode {_options.LockMode}.")
                    : new WormHealth(false, $"Bucket '{_options.Bucket}' has no Object Lock. Create the bucket with Object Lock enabled.");
            }
            catch (Exception ex)
            {
                return new WormHealth(false, $"Bucket '{_options.Bucket}' not usable: {ex.Message}");
            }
        }

        public void Dispose() => _client.Dispose();
    }
}
