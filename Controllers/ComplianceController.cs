using System.Globalization;
using System.Text;
using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Compliance;
using MailArchiver.Services.Organizations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MailArchiver.Controllers
{
    /// <summary>
    /// Retention, legal hold and archive integrity of the current organization. Admins change
    /// settings; auditors may look at everything, start a verification and export the chain.
    /// </summary>
    public class ComplianceController : Controller
    {
        private readonly MailArchiverDbContext _context;
        private readonly IOrganizationContext _scope;
        private readonly IOrganizationService _organizations;
        private readonly IOriginalArchiveService _originals;
        private readonly IWormStore _worm;
        private readonly IntegrityCheckQueue _queue;
        private readonly RetentionService _retention;
        private readonly DataProtectionService _dataProtection;
        private readonly ISelectedEmailsExportService? _selectedExport;
        private readonly IAuthenticationService _authService;
        private readonly IAccessLogService _accessLog;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IStringLocalizer<ComplianceResource> _l;
        private readonly ILogger<ComplianceController> _logger;

        public ComplianceController(MailArchiverDbContext context, IOrganizationContext scope, IOrganizationService organizations,
            IOriginalArchiveService originals, IWormStore worm, IntegrityCheckQueue queue, RetentionService retention,
            DataProtectionService dataProtection, IAuthenticationService authService, IAccessLogService accessLog,
            IServiceScopeFactory scopeFactory, IStringLocalizer<ComplianceResource> localizer,
            ILogger<ComplianceController> logger, ISelectedEmailsExportService? selectedExport = null)
        {
            _dataProtection = dataProtection;
            _selectedExport = selectedExport;
            _context = context;
            _scope = scope;
            _organizations = organizations;
            _originals = originals;
            _worm = worm;
            _queue = queue;
            _retention = retention;
            _authService = authService;
            _accessLog = accessLog;
            _scopeFactory = scopeFactory;
            _l = localizer;
            _logger = logger;
        }

        private string CurrentUser => _authService.GetCurrentUserDisplayName(HttpContext) ?? "unknown";

        private IActionResult NoOrganization()
        {
            TempData["ErrorMessage"] = _l["NoOrganization"].Value;
            return RedirectToAction("Index", "Home");
        }

        [AdminOrAuditorRequired]
        public async Task<IActionResult> Index()
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();

            var organization = await _organizations.GetAsync(organizationId);
            if (organization == null)
                return NoOrganization();

            var now = DateTime.UtcNow;
            var model = new ComplianceViewModel
            {
                Organization = organization,
                CanEdit = _authService.IsCurrentUserAdmin(HttpContext),
                TotalEmails = await _context.ArchivedEmails.LongCountAsync(),
                OriginalsBySource = await _context.ArchivedEmailOriginals
                    .Where(o => o.PurgedAt == null)
                    .GroupBy(o => o.Source)
                    .Select(g => new { g.Key, Count = g.LongCount() })
                    .ToDictionaryAsync(x => x.Key, x => x.Count),
                PurgedOriginals = await _context.ArchivedEmailOriginals.LongCountAsync(o => o.PurgedAt != null),
                EmailsWithoutOriginal = await _context.ArchivedEmails
                    .LongCountAsync(e => !_context.ArchivedEmailOriginals.Any(o => o.ArchivedEmailId == e.Id)),
                EmailsOnHold = await _context.ArchivedEmails.LongCountAsync(e => e.LegalHold),
                ExpiredNotPurged = await _retention.ExpiredEmails(organizationId).LongCountAsync(),
                NextExpiry = await _context.ArchivedEmails
                    .Where(e => e.RetainUntil != null && e.RetainUntil > now)
                    .MinAsync(e => e.RetainUntil),
                OriginalsHead = await _context.ArchivedEmailOriginals
                    .OrderByDescending(o => o.ChainSeq)
                    .Select(o => new ChainHead(o.ChainSeq, o.ChainHash, o.ArchivedAt))
                    .FirstOrDefaultAsync(),
                AuditHead = await _context.AccessLogs
                    .Where(l => l.OrganizationId == organizationId)
                    .OrderByDescending(l => l.ChainSeq)
                    .Select(l => new ChainHead(l.ChainSeq, l.ChainHash ?? string.Empty, l.Timestamp))
                    .FirstOrDefaultAsync(),
                Runs = await _context.IntegrityCheckRuns.AsNoTracking()
                    .OrderByDescending(r => r.StartedAt).Take(10).ToListAsync(),
                Anchors = await _context.IntegrityAnchors.AsNoTracking()
                    .OrderByDescending(a => a.CreatedAt).Take(10).ToListAsync(),
                Accounts = await _context.MailAccounts.AsNoTracking()
                    .OrderBy(a => a.Name)
                    .Select(a => new ComplianceAccountRow(a.Id, a.Name, a.EmailAddress, a.RetentionYears, a.LegalHold,
                        _context.ArchivedEmails.Count(e => e.MailAccountId == a.Id)))
                    .ToListAsync(),
                WormEnabled = _worm.IsEnabled,
                RestrictedEmails = await _context.ArchivedEmails.LongCountAsync(e => e.ProcessingRestricted),
                LastAuditPrune = await _context.AuditLogPrunes.AsNoTracking()
                    .OrderByDescending(p => p.PrunedThroughSeq).FirstOrDefaultAsync(),
                AuditLogRetentionYears = HttpContext.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<ComplianceOptions>>().Value.AuditLogRetentionYears
            };
            if (_worm.IsEnabled)
                model.WormStatus = (await _worm.CheckAsync(HttpContext.RequestAborted)).Message;

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminRequired]
        public async Task<IActionResult> Settings(int retentionYears, bool autoPurgeExpired, bool legalHold,
            string? legalHoldReason, bool allowProviderAccess)
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();
            if (retentionYears is < 0 or > 100)
            {
                TempData["ErrorMessage"] = _l["RetentionYearsRange"].Value;
                return RedirectToAction(nameof(Index));
            }
            if (legalHold && string.IsNullOrWhiteSpace(legalHoldReason))
            {
                TempData["ErrorMessage"] = _l["LegalHoldReasonRequired"].Value;
                return RedirectToAction(nameof(Index));
            }

            var before = await _organizations.GetAsync(organizationId);
            await _organizations.UpdateSettingsAsync(organizationId,
                new OrganizationSettings(retentionYears, autoPurgeExpired, legalHold, legalHoldReason?.Trim(), allowProviderAccess),
                CurrentUser);

            if (before != null && before.LegalHold != legalHold)
                QueueWormLegalHold(e => true, legalHold);

            TempData["SuccessMessage"] = _l["SettingsSaved"].Value;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminRequired]
        public async Task<IActionResult> Account(int id, int? retentionYears, bool legalHold)
        {
            var account = await _context.MailAccounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account == null)
                return NotFound();
            if (retentionYears is < 0 or > 100)
            {
                TempData["ErrorMessage"] = _l["RetentionYearsRange"].Value;
                return RedirectToAction(nameof(Index));
            }

            var holdChanged = account.LegalHold != legalHold;
            account.RetentionYears = retentionYears;
            account.LegalHold = legalHold;
            await _context.SaveChangesAsync();

            await _accessLog.LogAccessAsync(CurrentUser, holdChanged ? AccessLogType.LegalHold : AccessLogType.Account,
                searchParameters: $"Mail account {account.Name}: RetentionYears={(retentionYears?.ToString() ?? "organization default")}, LegalHold={legalHold}",
                mailAccountId: account.Id);

            if (holdChanged)
                QueueWormLegalHold(e => e.MailAccountId == id, legalHold);

            TempData["SuccessMessage"] = _l["SettingsSaved"].Value;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminRequired]
        [EmailAccessRequired]
        public async Task<IActionResult> EmailLegalHold(int id, bool hold, string? returnUrl)
        {
            var email = await _context.ArchivedEmails.FirstOrDefaultAsync(e => e.Id == id);
            if (email == null)
                return NotFound();

            email.LegalHold = hold;
            await _context.SaveChangesAsync();
            await _accessLog.LogAccessAsync(CurrentUser, AccessLogType.LegalHold, emailId: id,
                emailSubject: Truncate(email.Subject), emailFrom: Truncate(email.From),
                searchParameters: hold ? "Legal hold set" : "Legal hold released", mailAccountId: email.MailAccountId);

            QueueWormLegalHold(e => e.Id == id, hold);

            TempData["SuccessMessage"] = hold ? _l["LegalHoldSet"].Value : _l["LegalHoldReleased"].Value;
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Details", "Emails", new { id })!);
        }

        /// <summary>Downloads the original message exactly as it was archived.</summary>
        [EmailAccessRequired]
        public async Task<IActionResult> Original(int id)
        {
            var original = await _context.ArchivedEmailOriginals.AsNoTracking()
                .FirstOrDefaultAsync(o => o.ArchivedEmailId == id);
            if (original == null)
                return NotFound();

            var bytes = await _originals.ReadAsync(original, HttpContext.RequestAborted);
            if (bytes == null)
            {
                TempData["ErrorMessage"] = original.Source == OriginalSources.LegacySeal
                    ? _l["NoOriginalLegacy"].Value
                    : _l["OriginalPurged"].Value;
                return RedirectToAction("Details", "Emails", new { id });
            }
            if (IntegrityChain.Sha256Hex(bytes) != original.ContentSha256)
            {
                _logger.LogError("Original of email {EmailId} does not match its SHA-256", id);
                TempData["ErrorMessage"] = _l["OriginalHashMismatch"].Value;
                return RedirectToAction("Details", "Emails", new { id });
            }

            var email = await _context.ArchivedEmails.AsNoTracking()
                .Where(e => e.Id == id).Select(e => new { e.Subject, e.From, e.MailAccountId }).FirstOrDefaultAsync();
            await _accessLog.LogAccessAsync(CurrentUser, AccessLogType.Download, emailId: id,
                emailSubject: Truncate(email?.Subject), emailFrom: Truncate(email?.From),
                searchParameters: $"Original downloaded (SHA-256 {original.ContentSha256})", mailAccountId: email?.MailAccountId);

            Response.Headers["X-Content-SHA256"] = original.ContentSha256;
            return File(bytes, "message/rfc822", $"original-{id}-{original.ContentSha256[..12]}.eml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminOrAuditorRequired]
        public IActionResult Verify()
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();
            var queued = _queue.Enqueue(organizationId, CurrentUser);
            TempData[queued ? "SuccessMessage" : "ErrorMessage"] =
                queued ? _l["VerificationQueued"].Value : _l["VerificationQueueFull"].Value;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminRequired]
        public async Task<IActionResult> PurgeExpired()
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();
            var deleted = await _retention.PurgeExpiredAsync(organizationId, CurrentUser, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = _l["PurgedExpired", deleted].Value;
            return RedirectToAction(nameof(Index));
        }

        [AdminOrAuditorRequired]
        public async Task<IActionResult> Run(long id)
        {
            var run = await _context.IntegrityCheckRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            return run == null ? NotFound() : View(run);
        }

        /// <summary>
        /// The originals chain and the anchors as CSV, so an auditor can recompute every link with
        /// an independent tool (see docs/compliance.md for the canonical form).
        /// </summary>
        // ---------------------------------------------------------------
        // Data protection (GDPR)
        // ---------------------------------------------------------------

        /// <summary>Access request (Art. 15) and restricted emails (Art. 18) of the organization.</summary>
        [AdminOrAuditorRequired]
        public async Task<IActionResult> DataProtection(string? address)
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();

            var model = new DataProtectionViewModel
            {
                CanEdit = _authService.IsCurrentUserAdmin(HttpContext),
                Restricted = await _dataProtection.RestrictedEmailsAsync(HttpContext.RequestAborted)
            };
            if (!string.IsNullOrWhiteSpace(address))
            {
                model.Result = await _dataProtection.FindEmailsOfAsync(address, HttpContext.RequestAborted);
                await LogSubjectRequestAsync(organizationId, $"Access request lookup for {address.Trim()}: {model.Result.Total} emails");
            }
            return View(model);
        }

        /// <summary>CSV overview of all emails naming the address (Art. 15 Abs. 3).</summary>
        [AdminOrAuditorRequired]
        public async Task<IActionResult> SubjectAccessCsv(string address)
        {
            if (_scope.OrganizationId is not int organizationId || string.IsNullOrWhiteSpace(address))
                return NotFound();
            var result = await _dataProtection.FindEmailsOfAsync(address, HttpContext.RequestAborted);
            await LogSubjectRequestAsync(organizationId, $"Access request overview (CSV) for {result.Address}: {result.Total} emails");
            var name = string.Concat(result.Address.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@'));
            return File(DataProtectionService.OverviewCsv(result), "text/csv", $"access-request-{name}.csv");
        }

        /// <summary>Exports all emails naming the address as a ZIP of .eml files (Art. 15 Abs. 3, Art. 20).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminOrAuditorRequired]
        public async Task<IActionResult> SubjectAccessExport(string address)
        {
            if (_scope.OrganizationId is not int organizationId || string.IsNullOrWhiteSpace(address))
                return NotFound();
            if (_selectedExport == null)
            {
                TempData["ErrorMessage"] = _l["ExportUnavailable"].Value;
                return RedirectToAction(nameof(DataProtection), new { address });
            }

            // The export takes every match, not only the listed ones.
            var ids = await _dataProtection.FindEmailIdsOfAsync(address, HttpContext.RequestAborted);
            if (ids.Count == 0)
            {
                TempData["ErrorMessage"] = _l["NoEmailsForAddress"].Value;
                return RedirectToAction(nameof(DataProtection), new { address });
            }

            await LogSubjectRequestAsync(organizationId, $"Access request export (EML) for {address.Trim()}: {ids.Count} emails");
            var jobId = _selectedExport.QueueExport(ids, AccountExportFormat.EML, CurrentUser);
            TempData["SuccessMessage"] = _l["SubjectExportStarted", ids.Count].Value;
            return RedirectToAction("SelectedEmailsExportStatus", "Emails", new { jobId });
        }

        /// <summary>Restricts or releases the processing of emails (Art. 18).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AdminRequired]
        public async Task<IActionResult> Restrict(List<int> ids, bool restricted, string? reason, string? returnUrl)
        {
            if (_scope.OrganizationId == null)
                return NoOrganization();
            if (restricted && string.IsNullOrWhiteSpace(reason))
            {
                TempData["ErrorMessage"] = _l["RestrictionReasonRequired"].Value;
                return LocalRedirect(SafeReturnUrl(returnUrl));
            }
            var changed = await _dataProtection.SetRestrictionAsync(ids, restricted, reason?.Trim(), CurrentUser, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = (restricted ? _l["EmailsRestricted", changed] : _l["EmailsReleased", changed]).Value;
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        private string SafeReturnUrl(string? returnUrl) =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(DataProtection))!;

        private async Task LogSubjectRequestAsync(int organizationId, string text)
        {
            _context.AccessLogs.Add(new AccessLog
            {
                Username = CurrentUser,
                Type = AccessLogType.DataSubjectRequest,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = text
            });
            await _context.SaveChangesAsync();
        }

        [AdminOrAuditorRequired]
        public async Task<IActionResult> ChainExport()
        {
            if (_scope.OrganizationId is not int organizationId)
                return NoOrganization();

            var sb = new StringBuilder();
            sb.AppendLine("chain_seq;archived_at_utc;archived_at_epoch_us;archived_email_id;mail_account_id;organization_id;source;content_sha256;size_bytes;prev_chain_hash;chain_hash;purged_at_utc;purge_reason;message_id");
            long lastSeq = 0;
            while (true)
            {
                var batch = await _context.ArchivedEmailOriginals.AsNoTracking()
                    .Where(o => o.ChainSeq > lastSeq)
                    .OrderBy(o => o.ChainSeq)
                    .Take(5000)
                    .Select(o => new
                    {
                        o.ChainSeq, o.ArchivedAt, o.ArchivedEmailId, o.MailAccountId, o.OrganizationId, o.Source,
                        o.ContentSha256, o.SizeBytes, o.PrevChainHash, o.ChainHash, o.PurgedAt, o.PurgeReason, o.MessageId
                    })
                    .ToListAsync();
                if (batch.Count == 0)
                    break;
                foreach (var o in batch)
                {
                    sb.Append(o.ChainSeq).Append(';')
                        .Append(o.ArchivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)).Append(';')
                        .Append(IntegrityChain.EpochMicros(o.ArchivedAt)).Append(';')
                        .Append(o.ArchivedEmailId).Append(';')
                        .Append(o.MailAccountId).Append(';')
                        .Append(o.OrganizationId).Append(';')
                        .Append(o.Source).Append(';')
                        .Append(o.ContentSha256).Append(';')
                        .Append(o.SizeBytes).Append(';')
                        .Append(o.PrevChainHash).Append(';')
                        .Append(o.ChainHash).Append(';')
                        .Append(o.PurgedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append(';')
                        .Append(Csv(o.PurgeReason)).Append(';')
                        .Append(Csv(o.MessageId)).AppendLine();
                }
                lastSeq = batch[^1].ChainSeq;
            }

            await _accessLog.LogAccessAsync(CurrentUser, AccessLogType.AuditExport,
                searchParameters: $"Originals chain exported up to #{lastSeq}");
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                $"originals-chain-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        }

        /// <summary>
        /// Mirrors a legal hold change onto the WORM objects in the background (S3 legal hold),
        /// so that the hold also binds whoever controls the bucket.
        /// </summary>
        private void QueueWormLegalHold(System.Linq.Expressions.Expression<Func<ArchivedEmail, bool>> emails, bool on)
        {
            if (!_worm.IsEnabled || _scope.OrganizationId is not int organizationId)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await using var context = OrganizationService.CreateSystemContext(scope.ServiceProvider);
                    var worm = scope.ServiceProvider.GetRequiredService<IWormStore>();
                    var objects = await context.ArchivedEmails
                        .Where(e => e.OrganizationId == organizationId)
                        .Where(emails)
                        .Join(context.ArchivedEmailOriginals.Where(o => o.StorageKey != null && o.PurgedAt == null),
                            e => e.Id, o => o.ArchivedEmailId, (e, o) => new { o.StorageKey, o.StorageVersionId })
                        .Distinct()
                        .ToListAsync();
                    foreach (var o in objects)
                        await worm.SetLegalHoldAsync(o.StorageKey!, o.StorageVersionId, on);
                    _logger.LogInformation("WORM legal hold {State} on {Count} objects of organization {OrganizationId}",
                        on ? "set" : "released", objects.Count, organizationId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Applying the legal hold to WORM objects failed");
                }
            });
        }

        private static string? Truncate(string? value) =>
            value == null ? null : value.Length > 255 ? value[..255] : value;

        private static string Csv(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public record ChainHead(long Seq, string Hash, DateTime At);

    public record ComplianceAccountRow(int Id, string Name, string EmailAddress, int? RetentionYears, bool LegalHold, int Emails);

    public class DataProtectionViewModel
    {
        public bool CanEdit { get; set; }
        public SubjectAccessResult? Result { get; set; }
        public List<SubjectEmailRow> Restricted { get; set; } = new();
    }

    public class ComplianceViewModel
    {
        public Organization Organization { get; set; } = null!;
        public bool CanEdit { get; set; }
        public long TotalEmails { get; set; }
        public Dictionary<string, long> OriginalsBySource { get; set; } = new();
        public long PurgedOriginals { get; set; }
        public long EmailsWithoutOriginal { get; set; }
        public long EmailsOnHold { get; set; }
        public long ExpiredNotPurged { get; set; }
        public DateTime? NextExpiry { get; set; }
        public ChainHead? OriginalsHead { get; set; }
        public ChainHead? AuditHead { get; set; }
        public List<IntegrityCheckRun> Runs { get; set; } = new();
        public List<IntegrityAnchor> Anchors { get; set; } = new();
        public List<ComplianceAccountRow> Accounts { get; set; } = new();
        public bool WormEnabled { get; set; }
        public string? WormStatus { get; set; }
        public long RestrictedEmails { get; set; }
        public AuditLogPrune? LastAuditPrune { get; set; }
        public int AuditLogRetentionYears { get; set; }
    }
}
