using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services.Compliance
{
    /// <summary>
    /// Attaches the original message to an email that is about to be archived. Called by every
    /// write path (IMAP, Graph, EML/MBox import) right before the email is saved, so the email,
    /// its attachments and its original are stored in one transaction; the database then links
    /// the original into the organization's hash chain.
    /// </summary>
    public interface IOriginalArchiveService
    {
        /// <param name="raw">
        /// The message exactly as received. When null the message is serialized by MimeKit and
        /// recorded with source <see cref="OriginalSources.Serialized"/>.
        /// </param>
        Task AttachAsync(MailArchiverDbContext context, ArchivedEmail email, MailAccount account, byte[]? raw,
            MimeMessage? message, string source, CancellationToken cancellationToken = default);

        /// <summary>The original bytes, from the database or the WORM store; null once purged.</summary>
        Task<byte[]?> ReadAsync(ArchivedEmailOriginal original, CancellationToken cancellationToken = default);

        /// <summary>
        /// Purges the originals of emails that were deleted after their retention expired: the
        /// WORM objects are deleted (unless another original still uses them) and the bytes are
        /// removed, while the row stays as proof of what was archived and when it was deleted.
        /// </summary>
        Task<int> PurgeAsync(MailArchiverDbContext context, IReadOnlyCollection<int> deletedEmailIds, string reason,
            CancellationToken cancellationToken = default);
    }

    public class OriginalArchiveService : IOriginalArchiveService
    {
        /// <summary>Keeps originals in the database; used where no DI container is available (tests).</summary>
        public static readonly OriginalArchiveService DatabaseOnly = new(
            DisabledWormStore.Instance, Options.Create(new ArchiveStorageOptions()), NullLogger<OriginalArchiveService>.Instance);

        private readonly IWormStore _worm;
        private readonly ArchiveStorageOptions _options;
        private readonly ILogger<OriginalArchiveService> _logger;

        public OriginalArchiveService(IWormStore worm, IOptions<ArchiveStorageOptions> options, ILogger<OriginalArchiveService> logger)
        {
            _worm = worm;
            _options = options.Value;
            _logger = logger;
        }

        public async Task AttachAsync(MailArchiverDbContext context, ArchivedEmail email, MailAccount account, byte[]? raw,
            MimeMessage? message, string source, CancellationToken cancellationToken = default)
        {
            if (raw == null || raw.Length == 0)
            {
                if (message == null)
                    throw new ArgumentException("Either the raw bytes or the parsed message is required.");
                using var buffer = new MemoryStream();
                await message.WriteToAsync(FormatOptions.Default, buffer, cancellationToken);
                raw = buffer.ToArray();
                source = OriginalSources.Serialized;
            }

            var sha = IntegrityChain.Sha256Hex(raw);
            var original = new ArchivedEmailOriginal
            {
                Source = source,
                ContentSha256 = sha,
                SizeBytes = raw.LongLength,
                MessageId = email.MessageId,
                MailAccountId = account.Id
            };

            if (_worm.IsEnabled)
            {
                var organization = await context.Organizations.AsNoTracking()
                    .FirstAsync(o => o.Id == account.OrganizationId, cancellationToken);
                var years = account.RetentionYears ?? organization.RetentionYears;
                var retainUntil = RetentionCalculator.RetentionEnd(email.SentDate, email.ReceivedDate, years);
                var legalHold = account.LegalHold || organization.LegalHold || email.LegalHold;

                var key = StorageKeys.Original(organization.Slug, sha, DateTime.UtcNow);
                WormPutResult put;
                try
                {
                    put = await _worm.PutAsync(key, raw, retainUntil, legalHold, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Storing original {Key} in WORM storage failed", key);
                    throw new ArchiveStorageException($"Storing the original in WORM storage failed: {ex.Message}", ex);
                }
                original.StorageKey = put.Key;
                original.StorageVersionId = put.VersionId;
                if (_options.KeepDatabaseCopy)
                    original.RawMime = raw;
            }
            else
            {
                original.RawMime = raw;
            }

            email.Original = original;
        }

        public async Task<int> PurgeAsync(MailArchiverDbContext context, IReadOnlyCollection<int> deletedEmailIds, string reason,
            CancellationToken cancellationToken = default)
        {
            if (deletedEmailIds.Count == 0)
                return 0;
            var ids = deletedEmailIds.ToList();

            var originals = await context.ArchivedEmailOriginals
                .Where(o => ids.Contains(o.ArchivedEmailId) && o.PurgedAt == null)
                .Select(o => new { o.Id, o.StorageKey, o.StorageVersionId })
                .ToListAsync(cancellationToken);
            if (originals.Count == 0)
                return 0;

            if (_worm.IsEnabled)
            {
                var originalIds = originals.Select(o => o.Id).ToList();
                foreach (var stored in originals.Where(o => o.StorageKey != null)
                             .GroupBy(o => (o.StorageKey, o.StorageVersionId)).Select(g => g.Key))
                {
                    // Content addressed keys: the same message archived twice shares one object.
                    var stillUsed = await context.ArchivedEmailOriginals.AnyAsync(o =>
                        o.StorageKey == stored.StorageKey && o.PurgedAt == null && !originalIds.Contains(o.Id),
                        cancellationToken);
                    if (stillUsed)
                        continue;
                    try
                    {
                        await _worm.DeleteAsync(stored.StorageKey!, stored.StorageVersionId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // The object stays until someone removes it; its retention has expired.
                        _logger.LogWarning(ex, "Deleting expired WORM object {Key} failed", stored.StorageKey);
                    }
                }
            }

            var purgeIds = originals.Select(o => o.Id).ToArray();
            return await context.ArchivedEmailOriginals
                .Where(o => purgeIds.Contains(o.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(o => o.RawMime, (byte[]?)null)
                    .SetProperty(o => o.PurgedAt, DateTime.UtcNow)
                    .SetProperty(o => o.PurgeReason, reason), cancellationToken);
        }

        public async Task<byte[]?> ReadAsync(ArchivedEmailOriginal original, CancellationToken cancellationToken = default)
        {
            if (original.RawMime != null)
                return original.RawMime;
            if (original.PurgedAt != null || string.IsNullOrEmpty(original.StorageKey))
                return null;
            return await _worm.GetAsync(original.StorageKey, original.StorageVersionId, cancellationToken);
        }
    }

    /// <summary>The original could not be stored; the email must not count as archived.</summary>
    public class ArchiveStorageException : Exception
    {
        public ArchiveStorageException(string message, Exception inner) : base(message, inner) { }
    }

    public static class StorageKeys
    {
        public static string Original(string organizationSlug, string sha256, DateTime archivedUtc) =>
            $"{organizationSlug}/originals/{archivedUtc:yyyy}/{sha256[..2]}/{sha256}.eml";

        public static string Anchor(string organizationSlug, string chainKind, long seq, DateTime createdUtc) =>
            $"{organizationSlug}/anchors/{chainKind}/{createdUtc:yyyy}/{createdUtc:yyyyMMddTHHmmssZ}-{seq}.json";
    }

    /// <summary>The retention rule of MigrateFork_3 (ma_retention_end), in C#.</summary>
    public static class RetentionCalculator
    {
        /// <summary>
        /// End of retention: 01.01. 00:00 UTC of the year after (year of the email + years).
        /// Null when there is no retention obligation (0 years).
        /// </summary>
        public static DateTime? RetentionEnd(DateTime sentDate, DateTime receivedDate, int years)
        {
            if (years <= 0)
                return null;
            var year = Math.Max(sentDate.Year, receivedDate.Year);
            if (year + years + 1 > 9999)
                return DateTime.MaxValue;
            return new DateTime(year + years + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
