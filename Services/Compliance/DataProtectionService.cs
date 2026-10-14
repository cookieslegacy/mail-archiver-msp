using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Compliance
{
    /// <summary>One email found for a data subject access request.</summary>
    public record SubjectEmailRow(int Id, int MailAccountId, string Account, DateTime SentDate, string From, string To,
        string Cc, string Subject, int Attachments, bool HasOriginal, bool ProcessingRestricted);

    public record SubjectAccessResult(string Address, int Total, bool Truncated, List<SubjectEmailRow> Emails,
        Dictionary<string, int> PerAccount);

    /// <summary>
    /// The data protection (GDPR) functions of an organization: access requests (Art. 15),
    /// restriction of processing (Art. 18) and the retention of the audit log (Art. 5).
    /// Works through the caller's context, so it only ever sees that organization.
    /// </summary>
    public class DataProtectionService
    {
        /// <summary>Upper bound of emails listed for one request; the counts are always complete.</summary>
        public const int MaxListedEmails = 5000;

        private readonly MailArchiverDbContext _context;
        private readonly ComplianceOptions _options;
        private readonly ILogger<DataProtectionService> _logger;

        public DataProtectionService(MailArchiverDbContext context, IOptions<ComplianceOptions> options,
            ILogger<DataProtectionService> logger)
        {
            _context = context;
            _options = options.Value;
            _logger = logger;
        }

        // ---------------------------------------------------------------
        // Art. 15: access request
        // ---------------------------------------------------------------

        /// <summary>
        /// Every email of the organization that names the address as sender or recipient.
        /// Matching is a case-insensitive substring match on the address fields, so display
        /// names around the address do not matter.
        /// </summary>
        public async Task<SubjectAccessResult> FindEmailsOfAsync(string address, CancellationToken ct = default)
        {
            address = address.Trim();
            var pattern = $"%{EscapeLike(address)}%";

            var query = _context.ArchivedEmails.AsNoTracking()
                .Where(e => EF.Functions.ILike(e.From, pattern, "\\")
                            || EF.Functions.ILike(e.To, pattern, "\\")
                            || EF.Functions.ILike(e.Cc, pattern, "\\")
                            || EF.Functions.ILike(e.Bcc, pattern, "\\"));

            var total = await query.CountAsync(ct);
            var perAccount = await query
                .GroupBy(e => e.MailAccount.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            var rows = await query
                .OrderByDescending(e => e.SentDate)
                .Take(MaxListedEmails)
                .Select(e => new SubjectEmailRow(e.Id, e.MailAccountId, e.MailAccount.Name, e.SentDate, e.From, e.To, e.Cc,
                    e.Subject, e.Attachments.Count(), _context.ArchivedEmailOriginals.Any(o => o.ArchivedEmailId == e.Id),
                    e.ProcessingRestricted))
                .ToListAsync(ct);

            return new SubjectAccessResult(address, total, total > rows.Count, rows, perAccount);
        }

        /// <summary>Ids of every email naming the address, without the listing limit.</summary>
        public Task<List<int>> FindEmailIdsOfAsync(string address, CancellationToken ct = default)
        {
            var pattern = $"%{EscapeLike(address.Trim())}%";
            return _context.ArchivedEmails.AsNoTracking()
                .Where(e => EF.Functions.ILike(e.From, pattern, "\\")
                            || EF.Functions.ILike(e.To, pattern, "\\")
                            || EF.Functions.ILike(e.Cc, pattern, "\\")
                            || EF.Functions.ILike(e.Bcc, pattern, "\\"))
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .ToListAsync(ct);
        }

        /// <summary>CSV overview of a request (semicolon separated, UTF-8 with BOM for spreadsheets).</summary>
        public static byte[] OverviewCsv(SubjectAccessResult result)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Id;MailAccount;SentDate;From;To;Cc;Subject;Attachments;HasOriginal;ProcessingRestricted");
            foreach (var e in result.Emails)
            {
                csv.AppendLine(string.Join(";",
                    e.Id.ToString(), Csv(e.Account), e.SentDate.ToString("yyyy-MM-dd HH:mm:ss"), Csv(e.From), Csv(e.To), Csv(e.Cc),
                    Csv(e.Subject), e.Attachments.ToString(), e.HasOriginal ? "yes" : "no", e.ProcessingRestricted ? "yes" : "no"));
            }
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        }

        // ---------------------------------------------------------------
        // Art. 18: restriction of processing
        // ---------------------------------------------------------------

        /// <summary>Restricts or releases the emails; returns how many rows changed.</summary>
        public async Task<int> SetRestrictionAsync(IReadOnlyCollection<int> emailIds, bool restricted, string? reason,
            string actingUser, CancellationToken ct = default)
        {
            var emails = await _context.ArchivedEmails
                .Where(e => emailIds.Contains(e.Id) && e.ProcessingRestricted != restricted)
                .ToListAsync(ct);
            foreach (var email in emails)
            {
                email.ProcessingRestricted = restricted;
                email.RestrictionReason = restricted ? reason : null;
                email.RestrictedAt = restricted ? DateTime.UtcNow : null;
            }
            if (emails.Count == 0)
                return 0;

            _context.AccessLogs.Add(new AccessLog
            {
                Username = actingUser,
                Type = AccessLogType.ProcessingRestriction,
                Timestamp = DateTime.UtcNow,
                EmailId = emails.Count == 1 ? emails[0].Id : null,
                SearchParameters = (restricted ? "Processing restricted (Art. 18 GDPR)" : "Restriction of processing lifted")
                    + $" for {emails.Count} emails: {string.Join(",", emails.Take(50).Select(e => e.Id))}"
                    + (emails.Count > 50 ? ",…" : string.Empty)
                    + (restricted && !string.IsNullOrWhiteSpace(reason) ? $"; reason: {reason}" : string.Empty)
            });
            await _context.SaveChangesAsync(ct);
            return emails.Count;
        }

        public Task<List<SubjectEmailRow>> RestrictedEmailsAsync(CancellationToken ct = default) =>
            _context.ArchivedEmails.AsNoTracking()
                .Where(e => e.ProcessingRestricted)
                .OrderByDescending(e => e.RestrictedAt)
                .Take(MaxListedEmails)
                .Select(e => new SubjectEmailRow(e.Id, e.MailAccountId, e.MailAccount.Name, e.SentDate, e.From, e.To, e.Cc,
                    e.Subject, e.Attachments.Count(), _context.ArchivedEmailOriginals.Any(o => o.ArchivedEmailId == e.Id), true))
                .ToListAsync(ct);

        // ---------------------------------------------------------------
        // Art. 5: audit log retention
        // ---------------------------------------------------------------

        /// <summary>
        /// Prunes audit entries of the organization that are older than the configured
        /// retention; returns the number of removed entries. Done by the database in one
        /// transaction (<c>ma_audit_prune</c>), which records the cut for the verifier.
        /// Organization 0 is the provider level chain of entries without organization.
        /// </summary>
        public async Task<long> PruneAuditLogAsync(int organizationId, string actingUser, CancellationToken ct = default)
        {
            if (_options.AuditLogRetentionYears <= 0)
                return 0;

            // Like § 147 AO: the period starts with the end of the calendar year.
            var cutoff = new DateTime(DateTime.UtcNow.Year - _options.AuditLogRetentionYears, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var removed = await _context.Database
                .SqlQuery<long>($"SELECT mail_archiver.ma_audit_prune({organizationId}, {cutoff}, {actingUser}) AS \"Value\"")
                .SingleAsync(ct);

            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} audit log entries of organization {OrganizationId} older than {Cutoff:d}",
                    removed, organizationId, cutoff);
                _context.AccessLogs.Add(new AccessLog
                {
                    Username = actingUser,
                    Type = AccessLogType.AuditLogPrune,
                    Timestamp = DateTime.UtcNow,
                    OrganizationId = organizationId == 0 ? null : organizationId,
                    SearchParameters = $"Pruned {removed} audit log entries older than {cutoff:yyyy-MM-dd} (retention {_options.AuditLogRetentionYears} years)"
                });
                await _context.SaveChangesAsync(ct);
            }
            return removed;
        }

        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private static string Csv(string? value)
        {
            value ??= string.Empty;
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
                value = "'" + value;
            return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
