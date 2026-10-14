using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Organizations
{
    public interface IOrganizationService
    {
        Task<List<OrganizationOverview>> GetOverviewAsync();
        Task<Organization?> GetAsync(int id);
        Task<Organization> CreateAsync(Organization organization, string? adminUsername, string? adminEmail, string? adminPassword);
        Task UpdateByProviderAsync(Organization changes);
        Task UpdateSettingsAsync(int organizationId, OrganizationSettings settings, string actingUser);
        Task<bool> EnterSupportAccessAsync(int organizationId, string reason, string actingUser);
        void LeaveSupportAccess(string actingUser);
        Task<bool> EnterManagementAccessAsync(int organizationId, string actingUser);
        void LeaveManagementAccess(string actingUser);
    }

    /// <summary>What a provider admin sees of an organization: names and counts, no content.</summary>
    public record OrganizationOverview(
        Organization Organization,
        int MailAccounts,
        int Users,
        long Emails,
        long StorageBytes);

    /// <summary>Settings an organization's own admins control.</summary>
    public record OrganizationSettings(
        int RetentionYears,
        bool AutoPurgeExpired,
        bool LegalHold,
        string? LegalHoldReason,
        bool AllowProviderAccess);

    public class OrganizationService : IOrganizationService
    {
        private readonly MailArchiverDbContext _context;
        private readonly IServiceProvider _services;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IUserService _userService;
        private readonly ILogger<OrganizationService> _logger;

        public OrganizationService(MailArchiverDbContext context, IServiceProvider services,
            IHttpContextAccessor httpContextAccessor, IUserService userService, ILogger<OrganizationService> logger)
        {
            _context = context;
            _services = services;
            _httpContextAccessor = httpContextAccessor;
            _userService = userService;
            _logger = logger;
        }

        /// <summary>
        /// Creates a context that is not confined to an organization, for provider level
        /// aggregates. Never use it to hand out organization content to a user.
        /// </summary>
        public static MailArchiverDbContext CreateSystemContext(IServiceProvider services)
        {
            var options = services.GetRequiredService<DbContextOptions<MailArchiverDbContext>>();
            var protector = services.GetService<ICredentialProtector>() ?? NullCredentialProtector.Instance;
            return new MailArchiverDbContext(options, protector, SystemOrganizationContext.Instance);
        }

        /// <summary>Whether the user may sign in: provider admins always, others only in an active organization.</summary>
        public static async Task<bool> CanSignInAsync(MailArchiverDbContext context, User user)
        {
            if (user.IsProviderAdmin || user.OrganizationId == null)
                return true;
            return await context.Organizations.AnyAsync(o => o.Id == user.OrganizationId && o.IsActive);
        }

        /// <summary>The active organization whose mail domains contain the address, if any.</summary>
        public static async Task<int?> FindOrganizationForEmailAsync(MailArchiverDbContext context, string? email)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return null;
            var domain = email.Split('@', 2)[1].Trim().ToLowerInvariant();

            var candidates = await context.Organizations
                .Where(o => o.IsActive && o.EmailDomains != "")
                .ToListAsync();
            return candidates.FirstOrDefault(o => o.EmailDomainList.Contains(domain))?.Id;
        }

        public async Task<List<OrganizationOverview>> GetOverviewAsync()
        {
            await using var system = CreateSystemContext(_services);

            var organizations = await system.Organizations.Include(o => o.Package).OrderBy(o => o.Name).ToListAsync();
            var accounts = await system.MailAccounts
                .GroupBy(a => a.OrganizationId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            var users = await system.Users
                .Where(u => u.OrganizationId != null)
                .GroupBy(u => u.OrganizationId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            var emails = await system.ArchivedEmails
                .GroupBy(e => e.OrganizationId)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            var storage = await StorageUsage.ForOrganizationsAsync(system);

            return organizations.Select(o => new OrganizationOverview(
                o,
                accounts.GetValueOrDefault(o.Id),
                users.GetValueOrDefault(o.Id),
                emails.GetValueOrDefault(o.Id),
                storage.GetValueOrDefault(o.Id).Total)).ToList();
        }

        public Task<Organization?> GetAsync(int id) =>
            _context.Organizations.FirstOrDefaultAsync(o => o.Id == id);

        public async Task<Organization> CreateAsync(Organization organization, string? adminUsername,
            string? adminEmail, string? adminPassword)
        {
            organization.Slug = organization.Slug.Trim().ToLowerInvariant();
            organization.CreatedAt = DateTime.UtcNow;

            await using var transaction = await _context.Database.BeginTransactionAsync();
            _context.Organizations.Add(organization);
            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(adminUsername) && !string.IsNullOrWhiteSpace(adminPassword))
            {
                var admin = await _userService.CreateUserAsync(
                    adminUsername.Trim(),
                    string.IsNullOrWhiteSpace(adminEmail) ? adminUsername.Trim() : adminEmail.Trim(),
                    adminPassword,
                    isAdmin: true);
                admin.OrganizationId = organization.Id;
                await _context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            _logger.LogInformation("Organization {Name} ({Slug}) created with id {Id}",
                organization.Name, organization.Slug, organization.Id);
            return organization;
        }

        public async Task UpdateByProviderAsync(Organization changes)
        {
            var organization = await _context.Organizations.FirstAsync(o => o.Id == changes.Id);
            organization.Name = changes.Name;
            organization.IsActive = changes.IsActive;
            organization.EmailDomains = string.Join(",", changes.EmailDomainList);
            organization.Notes = changes.Notes;
            // The slug is part of storage keys in the WORM store and therefore immutable.
            await _context.SaveChangesAsync();
        }

        public async Task UpdateSettingsAsync(int organizationId, OrganizationSettings settings, string actingUser)
        {
            var organization = await _context.Organizations.FirstAsync(o => o.Id == organizationId);
            var holdChanged = organization.LegalHold != settings.LegalHold;
            var providerAccessChanged = organization.AllowProviderAccess != settings.AllowProviderAccess;

            organization.RetentionYears = settings.RetentionYears;
            organization.AutoPurgeExpired = settings.AutoPurgeExpired;
            organization.LegalHold = settings.LegalHold;
            organization.LegalHoldReason = settings.LegalHold ? settings.LegalHoldReason : null;
            organization.AllowProviderAccess = settings.AllowProviderAccess;

            _context.AccessLogs.Add(new AccessLog
            {
                Username = actingUser,
                Type = holdChanged ? AccessLogType.LegalHold : AccessLogType.Organization,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = $"RetentionYears={settings.RetentionYears}; AutoPurgeExpired={settings.AutoPurgeExpired}; " +
                    $"LegalHold={settings.LegalHold}; AllowProviderAccess={settings.AllowProviderAccess}" +
                    (holdChanged && settings.LegalHold ? $"; Reason={settings.LegalHoldReason}" : string.Empty) +
                    (providerAccessChanged ? "; provider access changed" : string.Empty)
            });
            await _context.SaveChangesAsync();
        }

        public async Task<bool> EnterSupportAccessAsync(int organizationId, string reason, string actingUser)
        {
            var http = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Support access requires a request.");

            var organization = await _context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
            if (organization == null || !organization.AllowProviderAccess)
                return false;

            // Written before the switch, into the audit chain of the organization that is entered.
            _context.AccessLogs.Add(new AccessLog
            {
                Username = actingUser,
                Type = AccessLogType.ProviderSupportAccess,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = $"Provider support access started. Reason: {reason}"
            });
            await _context.SaveChangesAsync();

            LeaveManagementAccess(actingUser);
            http.Session.SetInt32(OrganizationClaims.SupportOrganizationSessionKey, organizationId);
            _logger.LogWarning("Provider admin {User} entered organization {OrganizationId} for support: {Reason}",
                actingUser, organizationId, reason);
            return true;
        }

        public void LeaveSupportAccess(string actingUser)
        {
            var http = _httpContextAccessor.HttpContext;
            var organizationId = http?.Session.GetInt32(OrganizationClaims.SupportOrganizationSessionKey);
            if (http == null || organizationId == null)
                return;

            // Still inside the organization, so the entry lands in its audit chain.
            _context.AccessLogs.Add(new AccessLog
            {
                Username = actingUser,
                Type = AccessLogType.ProviderSupportAccess,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = "Provider support access ended."
            });
            _context.SaveChanges();

            http.Session.Remove(OrganizationClaims.SupportOrganizationSessionKey);
        }

        /// <summary>
        /// Lets a provider admin administer an organization's mail accounts without the
        /// organization's approval. Content stays closed (<see cref="ProviderManagementFilter"/>);
        /// start, end and every change are written into the organization's audit log.
        /// </summary>
        public async Task<bool> EnterManagementAccessAsync(int organizationId, string actingUser)
        {
            var http = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Management access requires a request.");
            if (http.Session.GetInt32(OrganizationClaims.SupportOrganizationSessionKey) != null)
                LeaveSupportAccess(actingUser);
            LeaveManagementAccess(actingUser);

            await using var system = CreateSystemContext(_services);
            if (!await system.Organizations.AnyAsync(o => o.Id == organizationId))
                return false;

            system.AccessLogs.Add(new AccessLog
            {
                Username = actingUser,
                Type = AccessLogType.Organization,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = "Provider management access started (mail accounts only, no email content)."
            });
            await system.SaveChangesAsync();

            http.Session.SetInt32(OrganizationClaims.ManagementOrganizationSessionKey, organizationId);
            _logger.LogInformation("Provider admin {User} manages organization {OrganizationId}", actingUser, organizationId);
            return true;
        }

        public void LeaveManagementAccess(string actingUser)
        {
            var http = _httpContextAccessor.HttpContext;
            var organizationId = http?.Session.GetInt32(OrganizationClaims.ManagementOrganizationSessionKey);
            if (http == null || organizationId == null)
                return;

            using (var system = CreateSystemContext(_services))
            {
                system.AccessLogs.Add(new AccessLog
                {
                    Username = actingUser,
                    Type = AccessLogType.Organization,
                    Timestamp = DateTime.UtcNow,
                    OrganizationId = organizationId,
                    SearchParameters = "Provider management access ended."
                });
                system.SaveChanges();
            }

            http.Session.Remove(OrganizationClaims.ManagementOrganizationSessionKey);
        }
    }
}
