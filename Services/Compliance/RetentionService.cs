using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Compliance
{
    /// <summary>Seals emails that were archived before originals were kept.</summary>
    public static class LegacySealing
    {
        /// <summary>File name, content type and payload SHA-256 of every attachment of an email.</summary>
        public static async Task<List<(string FileName, string ContentType, string PayloadSha256)>> AttachmentHashesAsync(
            MailArchiverDbContext context, int emailId, CancellationToken ct)
        {
            var rows = await context.EmailAttachments.AsNoTracking()
                .Where(a => a.ArchivedEmailId == emailId)
                .Select(a => new
                {
                    a.FileName,
                    a.ContentType,
                    Hash = a.AttachmentContent != null ? a.AttachmentContent.Hash : null,
                    a.LegacyContent
                })
                .ToListAsync(ct);

            return rows.Select(r => (
                    r.FileName ?? string.Empty,
                    r.ContentType ?? string.Empty,
                    r.Hash ?? (r.LegacyContent != null ? IntegrityChain.Sha256Hex(r.LegacyContent) : string.Empty)))
                .ToList();
        }
    }

    /// <summary>
    /// Retention housekeeping: stores RetainUntil for emails archived before retention existed,
    /// seals them into the hash chain, and deletes emails whose retention has expired.
    /// </summary>
    public class RetentionService
    {
        private readonly MailArchiverDbContext _context;
        private readonly IOriginalArchiveService _originals;
        private readonly ComplianceOptions _options;
        private readonly ILogger<RetentionService> _logger;

        public RetentionService(MailArchiverDbContext context, IOriginalArchiveService originals,
            IOptions<ComplianceOptions> options, ILogger<RetentionService> logger)
        {
            _context = context;
            _originals = originals;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>Stores the retention of one batch of old emails. Returns the number of rows updated.</summary>
        public async Task<int> BackfillRetentionAsync(CancellationToken ct = default)
        {
            // GREATEST(..., epoch): emails without retention obligation (-infinity) get a date
            // in the past, which every client can represent.
            return await _context.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE mail_archiver.""ArchivedEmails"" e
                SET ""RetainUntil"" = GREATEST(mail_archiver.ma_effective_retention(e), 'epoch'::timestamptz)
                WHERE e.""Id"" IN (
                    SELECT ""Id"" FROM mail_archiver.""ArchivedEmails""
                    WHERE ""RetainUntil"" IS NULL
                    ORDER BY ""Id""
                    LIMIT {_options.BatchSize})", ct);
        }

        /// <summary>
        /// Seals one batch of emails with an id above <paramref name="afterId"/> that have no
        /// original. Returns how many were sealed and the highest id looked at (null when done).
        /// </summary>
        public async Task<(int Sealed, int? LastId)> SealLegacyEmailsAsync(int afterId, CancellationToken ct = default)
        {
            var candidates = await _context.ArchivedEmails.AsNoTracking()
                .Where(e => e.Id > afterId)
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(Math.Min(_options.BatchSize, 500))
                .ToListAsync(ct);
            if (candidates.Count == 0)
                return (0, null);

            var emails = await _context.ArchivedEmails.AsNoTracking()
                .Where(e => candidates.Contains(e.Id) && !_context.ArchivedEmailOriginals.Any(o => o.ArchivedEmailId == e.Id))
                .OrderBy(e => e.Id)
                .ToListAsync(ct);

            foreach (var email in emails)
            {
                var attachments = await LegacySealing.AttachmentHashesAsync(_context, email.Id, ct);
                _context.ArchivedEmailOriginals.Add(new ArchivedEmailOriginal
                {
                    ArchivedEmailId = email.Id,
                    MailAccountId = email.MailAccountId,
                    MessageId = email.MessageId,
                    Source = OriginalSources.LegacySeal,
                    ContentSha256 = IntegrityChain.LegacySeal(email, attachments),
                    SizeBytes = 0
                });
            }
            if (emails.Count > 0)
            {
                await _context.SaveChangesAsync(ct);
                _context.ChangeTracker.Clear();
                _logger.LogInformation("Sealed {Count} emails archived without an original", emails.Count);
            }
            return (emails.Count, candidates[^1]);
        }

        /// <summary>Emails of the organization whose retention expired and that are not on hold.</summary>
        public IQueryable<ArchivedEmail> ExpiredEmails(int organizationId)
        {
            var now = DateTime.UtcNow;
            return _context.ArchivedEmails
                .Where(e => e.OrganizationId == organizationId
                            && e.RetainUntil != null && e.RetainUntil <= now
                            && !e.LegalHold
                            && !e.MailAccount.LegalHold
                            && !_context.Organizations.Any(o => o.Id == e.OrganizationId && o.LegalHold));
        }

        /// <summary>
        /// Deletes emails whose retention expired (the database checks every row again) and purges
        /// their originals. Returns the number of emails deleted.
        /// </summary>
        public async Task<int> PurgeExpiredAsync(int organizationId, string actingUser, CancellationToken ct = default)
        {
            var total = 0;
            while (!ct.IsCancellationRequested)
            {
                var ids = await ExpiredEmails(organizationId)
                    .OrderBy(e => e.Id)
                    .Select(e => e.Id)
                    .Take(_options.BatchSize)
                    .ToArrayAsync(ct);
                if (ids.Length == 0)
                    break;

                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                // Recorded on the purged originals by ma_email_purge_original.
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('mail_archiver.purge_reason', 'Retention period expired', true)", ct);
                // The upstream deletion lock (DeletionPolicy) does not apply to expired retention.
                await _context.ArchivedEmails.Where(e => ids.Contains(e.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsLocked, false), ct);
                var deleted = await _context.ArchivedEmails.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
                await transaction.CommitAsync(ct);

                await _originals.PurgeAsync(_context, ids, "Retention period expired", ct);
                total += deleted;
            }

            if (total > 0)
            {
                _context.AccessLogs.Add(new AccessLog
                {
                    Username = actingUser,
                    Type = AccessLogType.RetentionPurge,
                    Timestamp = DateTime.UtcNow,
                    OrganizationId = organizationId,
                    SearchParameters = $"Deleted {total} emails whose retention period had expired; originals purged, chain entries kept"
                });
                await _context.SaveChangesAsync(ct);
                _logger.LogInformation("Purged {Count} expired emails of organization {OrganizationId}", total, organizationId);
            }
            return total;
        }
    }
}
