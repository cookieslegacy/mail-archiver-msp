using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.ViewComponents
{
    /// <summary>Original, hash chain, retention and legal hold of one email, on its details page.</summary>
    public class EmailComplianceViewComponent : ViewComponent
    {
        private readonly MailArchiverDbContext _context;
        private readonly IAuthenticationService _authService;

        public EmailComplianceViewComponent(MailArchiverDbContext context, IAuthenticationService authService)
        {
            _context = context;
            _authService = authService;
        }

        public async Task<IViewComponentResult> InvokeAsync(int emailId)
        {
            var email = await _context.ArchivedEmails.AsNoTracking()
                .Where(e => e.Id == emailId)
                .Select(e => new
                {
                    e.Id, e.RetainUntil, e.LegalHold, AccountHold = e.MailAccount.LegalHold, e.OrganizationId,
                    e.ProcessingRestricted, e.RestrictionReason, e.RestrictedAt
                })
                .FirstOrDefaultAsync();
            if (email == null)
                return Content(string.Empty);

            var original = await _context.ArchivedEmailOriginals.AsNoTracking()
                .Where(o => o.ArchivedEmailId == emailId)
                .Select(o => new { o.Source, o.ContentSha256, o.SizeBytes, o.ChainSeq, o.ChainHash, o.ArchivedAt, o.StorageKey })
                .FirstOrDefaultAsync();
            var organizationHold = await _context.Organizations.AnyAsync(o => o.Id == email.OrganizationId && o.LegalHold);

            return View(new EmailComplianceModel
            {
                EmailId = email.Id,
                RetainUntil = email.RetainUntil,
                LegalHold = email.LegalHold,
                InheritedHold = email.AccountHold || organizationHold,
                HasOriginal = original != null,
                Source = original?.Source,
                Sha256 = original?.ContentSha256,
                SizeBytes = original?.SizeBytes ?? 0,
                ChainSeq = original?.ChainSeq ?? 0,
                ChainHash = original?.ChainHash,
                ArchivedAt = original?.ArchivedAt,
                InWorm = original?.StorageKey != null,
                CanSetHold = _authService.IsCurrentUserAdmin(HttpContext),
                ProcessingRestricted = email.ProcessingRestricted,
                RestrictionReason = email.RestrictionReason,
                RestrictedAt = email.RestrictedAt
            });
        }
    }

    public class EmailComplianceModel
    {
        public int EmailId { get; set; }
        public DateTime? RetainUntil { get; set; }
        public bool LegalHold { get; set; }
        public bool InheritedHold { get; set; }
        public bool HasOriginal { get; set; }
        public string? Source { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public long ChainSeq { get; set; }
        public string? ChainHash { get; set; }
        public DateTime? ArchivedAt { get; set; }
        public bool InWorm { get; set; }
        public bool CanSetHold { get; set; }
        public bool ProcessingRestricted { get; set; }
        public string? RestrictionReason { get; set; }
        public DateTime? RestrictedAt { get; set; }
        public bool HasBytes => HasOriginal && Source != OriginalSources.LegacySeal;
    }
}
