using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Compliance
{
    /// <summary>The document that an anchor stands for; written to WORM storage and time stamped.</summary>
    public static class AnchorDocument
    {
        public static byte[] Build(IntegrityAnchor anchor)
        {
            var created = DateTime.SpecifyKind(anchor.CreatedAt.ToUniversalTime(), DateTimeKind.Utc);
            // Fixed field order and formatting, so the document can be rebuilt byte for byte.
            var json = "{" +
                       $"\"format\":\"mail-archiver-anchor-v1\"," +
                       $"\"organizationId\":{anchor.OrganizationId.ToString(CultureInfo.InvariantCulture)}," +
                       $"\"chain\":\"{anchor.ChainKind}\"," +
                       $"\"seq\":{anchor.ChainSeq.ToString(CultureInfo.InvariantCulture)}," +
                       $"\"hash\":\"{anchor.ChainHash}\"," +
                       $"\"createdAt\":\"{created.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)}\"" +
                       "}\n";
            return Encoding.UTF8.GetBytes(json);
        }
    }

    /// <summary>
    /// Anchors the heads of an organization's chains: a small document with sequence number and
    /// hash, written to WORM storage and optionally time stamped by an RFC 3161 authority. Anyone
    /// who rewrites a chain afterwards (even with full database access) no longer matches it.
    /// </summary>
    public class AnchorService
    {
        private readonly MailArchiverDbContext _context;
        private readonly IWormStore _worm;
        private readonly ComplianceOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AnchorService> _logger;

        public AnchorService(MailArchiverDbContext context, IWormStore worm, IOptions<ComplianceOptions> options,
            IHttpClientFactory httpClientFactory, ILogger<AnchorService> logger)
        {
            _context = context;
            _worm = worm;
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<List<IntegrityAnchor>> AnchorAsync(int organizationId, CancellationToken cancellationToken = default)
        {
            var organization = await _context.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId, cancellationToken);
            var created = new List<IntegrityAnchor>();

            var originalsHead = await _context.ArchivedEmailOriginals.AsNoTracking()
                .Where(o => o.OrganizationId == organizationId)
                .OrderByDescending(o => o.ChainSeq)
                .Select(o => new { o.ChainSeq, o.ChainHash })
                .FirstOrDefaultAsync(cancellationToken);
            if (originalsHead != null)
            {
                var anchor = await AnchorHeadAsync(organization, IntegrityChainKinds.Originals,
                    originalsHead.ChainSeq, originalsHead.ChainHash, cancellationToken);
                if (anchor != null)
                    created.Add(anchor);
            }

            var auditHead = await _context.AccessLogs.AsNoTracking()
                .Where(l => l.OrganizationId == organizationId)
                .OrderByDescending(l => l.ChainSeq)
                .Select(l => new { l.ChainSeq, l.ChainHash })
                .FirstOrDefaultAsync(cancellationToken);
            if (auditHead?.ChainHash != null)
            {
                var anchor = await AnchorHeadAsync(organization, IntegrityChainKinds.Audit,
                    auditHead.ChainSeq, auditHead.ChainHash, cancellationToken);
                if (anchor != null)
                    created.Add(anchor);
            }

            return created;
        }

        private async Task<IntegrityAnchor?> AnchorHeadAsync(Organization organization, string kind, long seq, string hash,
            CancellationToken cancellationToken)
        {
            var alreadyAnchored = await _context.IntegrityAnchors.AnyAsync(a =>
                a.OrganizationId == organization.Id && a.ChainKind == kind && a.ChainSeq == seq, cancellationToken);
            if (alreadyAnchored)
                return null;

            var now = DateTime.UtcNow;
            var anchor = new IntegrityAnchor
            {
                OrganizationId = organization.Id,
                ChainKind = kind,
                ChainSeq = seq,
                ChainHash = hash,
                // Microsecond precision, the precision PostgreSQL keeps, so the document can be rebuilt.
                CreatedAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc)
            };
            var document = AnchorDocument.Build(anchor);

            if (_worm.IsEnabled)
            {
                // Anchors outlive the data they vouch for by a year.
                var retainUntil = now.AddYears(Math.Max(organization.RetentionYears, 1) + 1);
                var key = StorageKeys.Anchor(organization.Slug, kind, seq, now);
                var put = await _worm.PutAsync(key, document, retainUntil, legalHold: false, cancellationToken);
                anchor.StorageKey = put.Key;
                anchor.StorageVersionId = put.VersionId;
            }

            if (!string.IsNullOrWhiteSpace(_options.TimestampAuthorityUrl))
            {
                try
                {
                    anchor.TimestampToken = await RequestTimestampAsync(document, cancellationToken);
                    anchor.TimestampAuthority = _options.TimestampAuthorityUrl;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RFC 3161 time stamp from {Tsa} failed; anchor kept without time stamp",
                        _options.TimestampAuthorityUrl);
                }
            }

            _context.IntegrityAnchors.Add(anchor);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Anchored {Kind} chain of organization {OrganizationId} at #{Seq}", kind, organization.Id, seq);
            return anchor;
        }

        private async Task<byte[]> RequestTimestampAsync(byte[] document, CancellationToken cancellationToken)
        {
            var request = Rfc3161TimestampRequest.CreateFromData(document, HashAlgorithmName.SHA256,
                requestedPolicyId: null, nonce: RandomNumberGenerator.GetBytes(8), requestSignerCertificates: true);

            using var content = new ByteArrayContent(request.Encode());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");

            var client = _httpClientFactory.CreateClient(nameof(AnchorService));
            using var response = await client.PostAsync(_options.TimestampAuthorityUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            var token = request.ProcessResponse(body, out _);
            if (!token.VerifySignatureForData(document, out _))
                throw new CryptographicException("The time stamp authority returned a token that does not verify.");
            return token.AsSignedCms().Encode();
        }
    }
}
