using System.ComponentModel.DataAnnotations;
using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Organizations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MailArchiver.Controllers
{
    /// <summary>
    /// Organization (tenant) management for provider admins. Shows names and counts only, never
    /// content. Every change concerning an organization is written into that organization's
    /// audit chain, so its own admins can see what the provider did.
    /// </summary>
    [ProviderAdminRequired]
    public class OrganizationsController : Controller
    {
        private readonly IOrganizationService _organizations;
        private readonly IQuotaService _quota;
        private readonly IUserService _userService;
        private readonly IAuthenticationService _authService;
        private readonly IServiceProvider _services;
        private readonly IStringLocalizer<ComplianceResource> _l;
        private readonly ILogger<OrganizationsController> _logger;

        public OrganizationsController(IOrganizationService organizations, IQuotaService quota, IUserService userService,
            IAuthenticationService authService, IServiceProvider services,
            IStringLocalizer<ComplianceResource> localizer, ILogger<OrganizationsController> logger)
        {
            _organizations = organizations;
            _quota = quota;
            _userService = userService;
            _authService = authService;
            _services = services;
            _l = localizer;
            _logger = logger;
        }

        private string CurrentUser => _authService.GetCurrentUserDisplayName(HttpContext) ?? "unknown";

        public async Task<IActionResult> Index()
        {
            ViewBag.SupportOrganizationId = HttpContext.Session.GetInt32(OrganizationClaims.SupportOrganizationSessionKey);
            ViewBag.ManagementOrganizationId = HttpContext.Session.GetInt32(OrganizationClaims.ManagementOrganizationSessionKey);
            ViewBag.Quotas = await _quota.GetAllAsync();
            return View(await _organizations.GetOverviewAsync());
        }

        public async Task<IActionResult> Create()
        {
            ViewBag.Packages = await ActivePackagesAsync();
            return View(new OrganizationCreateViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(OrganizationCreateViewModel model)
        {
            model.Slug = (model.Slug ?? string.Empty).Trim().ToLowerInvariant();
            await using (var system = OrganizationService.CreateSystemContext(_services))
            {
                if (await system.Organizations.AnyAsync(o => o.Slug == model.Slug))
                    ModelState.AddModelError(nameof(model.Slug), _l["SlugTaken"]);
                var adminEmail = (string.IsNullOrWhiteSpace(model.AdminEmail) ? model.AdminUsername : model.AdminEmail)?.Trim().ToLower();
                if (!string.IsNullOrWhiteSpace(model.AdminUsername)
                    && await system.Users.AnyAsync(u => u.Username.ToLower() == model.AdminUsername.Trim().ToLower()
                                                        || u.Email.ToLower() == adminEmail))
                    ModelState.AddModelError(nameof(model.AdminUsername), _l["UsernameTaken"]);
            }
            if (!string.IsNullOrWhiteSpace(model.AdminUsername) && (model.AdminPassword?.Length ?? 0) < 12)
                ModelState.AddModelError(nameof(model.AdminPassword), _l["AdminPasswordTooShort"]);

            if (!ModelState.IsValid)
            {
                ViewBag.Packages = await ActivePackagesAsync();
                return View(model);
            }

            var organization = await _organizations.CreateAsync(new Organization
            {
                Name = model.Name.Trim(),
                Slug = model.Slug,
                EmailDomains = model.EmailDomains ?? string.Empty,
                RetentionYears = model.RetentionYears,
                PackageId = model.PackageId,
                Notes = model.Notes
            }, model.AdminUsername, model.AdminEmail, model.AdminPassword);

            await WriteOrganizationLogAsync(organization.Id,
                $"Organization created by provider admin {CurrentUser}" +
                (string.IsNullOrWhiteSpace(model.AdminUsername) ? string.Empty : $"; first admin: {model.AdminUsername.Trim()}"));

            TempData["SuccessMessage"] = _l["OrganizationCreated", organization.Name].Value;
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int id)
        {
            var organization = await _organizations.GetAsync(id);
            if (organization == null)
                return NotFound();

            await using var system = OrganizationService.CreateSystemContext(_services);
            ViewBag.Packages = await system.Packages.AsNoTracking()
                .Where(p => p.IsActive || p.Id == organization.PackageId)
                .OrderBy(p => p.Name).ToListAsync();
            ViewBag.Quota = await _quota.GetStatusAsync(id);
            ViewBag.MailAccounts = await MailAccountRowsAsync(system, id);
            ViewBag.Admins = await system.Users.AsNoTracking()
                .Where(u => u.OrganizationId == id && u.IsAdmin)
                .OrderBy(u => u.Username)
                .Select(u => new OrganizationAdminRow(u.Id, u.Username, u.Email, u.IsActive, u.OAuthRemoteUserId != null))
                .ToListAsync();
            ViewBag.Unassigned = await system.Users.AsNoTracking()
                .Where(u => u.OrganizationId == null && !u.IsProviderAdmin)
                .OrderBy(u => u.Username)
                .Select(u => new OrganizationAdminRow(u.Id, u.Username, u.Email, u.IsActive, u.OAuthRemoteUserId != null))
                .ToListAsync();
            return View(organization);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,IsActive,EmailDomains,Notes")] Organization model)
        {
            if (id != model.Id)
                return NotFound();
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                TempData["ErrorMessage"] = _l["NameRequired"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }
            if (id == Organization.DefaultOrganizationId && !model.IsActive)
            {
                TempData["ErrorMessage"] = _l["DefaultOrganizationStaysActive"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            await _organizations.UpdateByProviderAsync(model);
            await WriteOrganizationLogAsync(id,
                $"Organization changed by provider admin {CurrentUser}: name={model.Name}, active={model.IsActive}, domains={model.EmailDomains}");
            TempData["SuccessMessage"] = _l["OrganizationSaved"].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        /// <summary>Creates an additional admin for an organization, e.g. when its admins are locked out.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateAdmin(int id, string username, string email, string password)
        {
            if (await _organizations.GetAsync(id) == null)
                return NotFound();
            if (string.IsNullOrWhiteSpace(username) || (password?.Length ?? 0) < 12)
            {
                TempData["ErrorMessage"] = _l["AdminPasswordTooShort"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            await using var system = OrganizationService.CreateSystemContext(_services);
            if (await system.Users.AnyAsync(u => u.Username.ToLower() == username.Trim().ToLower()
                                                 || u.Email.ToLower() == (string.IsNullOrWhiteSpace(email) ? username : email).Trim().ToLower()))
            {
                TempData["ErrorMessage"] = _l["UsernameTaken"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            system.Users.Add(new User
            {
                Username = username.Trim(),
                Email = (string.IsNullOrWhiteSpace(email) ? username : email).Trim(),
                PasswordHash = _userService.HashPassword(password!),
                IsAdmin = true,
                IsActive = true,
                OrganizationId = id,
                CreatedAt = DateTime.UtcNow
            });
            await system.SaveChangesAsync();

            await WriteOrganizationLogAsync(id, $"Admin {username.Trim()} created by provider admin {CurrentUser}");
            TempData["SuccessMessage"] = _l["AdminCreated", username.Trim()].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        /// <summary>Puts a user without organization (e.g. a new OIDC user of an unknown domain) into this one.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignUser(int id, int userId)
        {
            if (await _organizations.GetAsync(id) == null)
                return NotFound();

            await using var system = OrganizationService.CreateSystemContext(_services);
            var user = await system.Users.FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == null && !u.IsProviderAdmin);
            if (user == null)
                return NotFound();
            user.OrganizationId = id;
            await system.SaveChangesAsync();

            await WriteOrganizationLogAsync(id, $"User {user.Username} assigned to the organization by provider admin {CurrentUser}");
            TempData["SuccessMessage"] = _l["UserAssigned", user.Username].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnterSupport(int id, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                TempData["ErrorMessage"] = _l["SupportReasonRequired"].Value;
                return RedirectToAction(nameof(Index));
            }
            if (!await _organizations.EnterSupportAccessAsync(id, reason.Trim(), CurrentUser))
            {
                TempData["ErrorMessage"] = _l["SupportAccessNotAllowed"].Value;
                return RedirectToAction(nameof(Index));
            }
            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult LeaveSupport()
        {
            _organizations.LeaveSupportAccess(CurrentUser);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Books a package and sets individual limits.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPackage(int id, int? packageId, decimal? storageQuotaGb, int? maxMailAccounts)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            var organization = await system.Organizations.FirstOrDefaultAsync(o => o.Id == id);
            if (organization == null)
                return NotFound();
            if (packageId != null && !await system.Packages.AnyAsync(p => p.Id == packageId))
                return NotFound();
            if (storageQuotaGb is < 0 || maxMailAccounts is < 0)
            {
                TempData["ErrorMessage"] = _l["QuotaMustBePositive"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            organization.PackageId = packageId;
            organization.StorageQuotaBytes = storageQuotaGb is decimal gb ? GigabytesToBytes(gb) : null;
            organization.MaxMailAccounts = maxMailAccounts;
            await system.SaveChangesAsync();

            var packageName = packageId == null ? "-" : await system.Packages.Where(p => p.Id == packageId).Select(p => p.Name).FirstAsync();
            await WriteOrganizationLogAsync(id,
                $"Package changed by provider admin {CurrentUser}: package={packageName}, " +
                $"storage quota={(storageQuotaGb?.ToString() ?? "package")} GB, max mail accounts={(maxMailAccounts?.ToString() ?? "package")}");
            TempData["SuccessMessage"] = _l["PackageSaved"].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        /// <summary>Opens an organization's mail account administration (no email content, no approval needed).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnterManagement(int id)
        {
            if (!await _organizations.EnterManagementAccessAsync(id, CurrentUser))
                return NotFound();
            return RedirectToAction("Index", "MailAccounts");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult LeaveManagement()
        {
            var id = HttpContext.Session.GetInt32(OrganizationClaims.ManagementOrganizationSessionKey);
            _organizations.LeaveManagementAccess(CurrentUser);
            return id == null ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Edit), new { id });
        }

        // ---------------------------------------------------------------
        // Packages
        // ---------------------------------------------------------------

        public async Task<IActionResult> Packages()
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            ViewBag.Bookings = await system.Organizations.Where(o => o.PackageId != null)
                .GroupBy(o => o.PackageId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            return View(await system.Packages.AsNoTracking().OrderBy(p => p.Name).ToListAsync());
        }

        public async Task<IActionResult> EditPackage(int? id)
        {
            if (id == null)
                return View(new PackageViewModel());
            await using var system = OrganizationService.CreateSystemContext(_services);
            var package = await system.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            return package == null ? NotFound() : View(PackageViewModel.From(package));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditPackage(PackageViewModel model)
        {
            model.Name = (model.Name ?? string.Empty).Trim();
            await using var system = OrganizationService.CreateSystemContext(_services);
            if (await system.Packages.AnyAsync(p => p.Name == model.Name && p.Id != model.Id))
                ModelState.AddModelError(nameof(model.Name), _l["PackageNameTaken"]);
            if (!ModelState.IsValid)
                return View(model);

            Package package;
            if (model.Id == 0)
            {
                package = new Package { CreatedAt = DateTime.UtcNow };
                system.Packages.Add(package);
            }
            else
            {
                package = await system.Packages.FirstOrDefaultAsync(p => p.Id == model.Id) ?? throw new KeyNotFoundException();
            }
            package.Name = model.Name;
            package.Description = model.Description;
            package.StorageQuotaBytes = model.StorageQuotaGb is decimal gb ? GigabytesToBytes(gb) : null;
            package.MaxMailAccounts = model.MaxMailAccounts;
            package.BlockArchivingOverQuota = model.BlockArchivingOverQuota;
            package.IsActive = model.IsActive;
            await system.SaveChangesAsync();

            _logger.LogInformation("Package {Name} saved by {User}", package.Name, CurrentUser);
            TempData["SuccessMessage"] = _l["PackageSaved"].Value;
            return RedirectToAction(nameof(Packages));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePackage(int id)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            if (await system.Organizations.AnyAsync(o => o.PackageId == id))
            {
                TempData["ErrorMessage"] = _l["PackageInUse"].Value;
                return RedirectToAction(nameof(Packages));
            }
            var package = await system.Packages.FirstOrDefaultAsync(p => p.Id == id);
            if (package == null)
                return NotFound();
            system.Packages.Remove(package);
            await system.SaveChangesAsync();
            TempData["SuccessMessage"] = _l["PackageDeleted"].Value;
            return RedirectToAction(nameof(Packages));
        }

        /// <summary>
        /// Usage of every organization in a month as CSV (semicolon separated, for billing):
        /// peak, average and last value of the daily usage rows.
        /// </summary>
        public async Task<IActionResult> UsageCsv(int? year, int? month)
        {
            var now = DateTime.UtcNow;
            var first = new DateOnly(year ?? now.Year, Math.Clamp(month ?? now.Month, 1, 12), 1);
            var last = first.AddMonths(1).AddDays(-1);

            await using var system = OrganizationService.CreateSystemContext(_services);
            var organizations = await system.Organizations.AsNoTracking().Include(o => o.Package).OrderBy(o => o.Name).ToListAsync();
            var snapshots = (await system.OrganizationUsageSnapshots.AsNoTracking()
                    .Where(u => u.Day >= first && u.Day <= last)
                    .ToListAsync())
                .GroupBy(u => u.OrganizationId)
                .ToDictionary(g => g.Key, g => g.OrderBy(u => u.Day).ToList());

            static string Gb(double bytes) => (bytes / (1L << 30)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Organization;Slug;Month;Package;QuotaGB;PeakGB;AverageGB;LastGB;MaxMailAccounts;LastEmails;DaysRecorded");
            foreach (var o in organizations)
            {
                var rows = snapshots.GetValueOrDefault(o.Id) ?? new List<OrganizationUsageSnapshot>();
                var lastRow = rows.LastOrDefault();
                csv.AppendLine(string.Join(";",
                    CsvField(o.Name), CsvField(o.Slug), first.ToString("yyyy-MM"),
                    CsvField(lastRow?.PackageName ?? o.Package?.Name ?? string.Empty),
                    lastRow?.StorageQuotaBytes is long q ? Gb(q) : o.EffectiveStorageQuotaBytes is long q2 ? Gb(q2) : string.Empty,
                    rows.Count > 0 ? Gb(rows.Max(r => r.StorageBytes)) : string.Empty,
                    rows.Count > 0 ? Gb(rows.Average(r => (double)r.StorageBytes)) : string.Empty,
                    lastRow != null ? Gb(lastRow.StorageBytes) : string.Empty,
                    rows.Count > 0 ? rows.Max(r => r.MailAccounts).ToString() : string.Empty,
                    lastRow?.Emails.ToString() ?? string.Empty,
                    rows.Count.ToString()));
            }
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return File(bytes, "text/csv", $"usage-{first:yyyy-MM}.csv");
        }

        private static string CsvField(string value)
        {
            // Names typed by people must not turn into spreadsheet formulas.
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
                value = "'" + value;
            return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static long GigabytesToBytes(decimal gb) => (long)Math.Round(gb * (1L << 30));

        private async Task<List<Package>> ActivePackagesAsync()
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            return await system.Packages.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync();
        }

        private static async Task<List<OrganizationMailAccountRow>> MailAccountRowsAsync(MailArchiverDbContext system, int organizationId)
        {
            var accounts = await system.MailAccounts.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId)
                .OrderBy(a => a.Name)
                .Select(a => new { a.Id, a.Name, a.EmailAddress, a.Provider, a.IsEnabled, a.LastSync })
                .ToListAsync();
            var ids = accounts.Select(a => a.Id).ToList();
            var emails = await system.ArchivedEmails
                .Where(e => ids.Contains(e.MailAccountId))
                .GroupBy(e => e.MailAccountId)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            var storage = await StorageUsage.ForAccountsAsync(system, ids);
            return accounts.Select(a => new OrganizationMailAccountRow(a.Id, a.Name, a.EmailAddress, a.Provider.ToString(),
                a.IsEnabled, a.LastSync, emails.GetValueOrDefault(a.Id), storage.GetValueOrDefault(a.Id))).ToList();
        }

        private async Task WriteOrganizationLogAsync(int organizationId, string text)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            system.AccessLogs.Add(new AccessLog
            {
                Username = CurrentUser,
                Type = AccessLogType.Organization,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = text
            });
            await system.SaveChangesAsync();
            _logger.LogInformation("Organization {OrganizationId}: {Text}", organizationId, text);
        }
    }

    public record OrganizationAdminRow(int Id, string Username, string Email, bool IsActive, bool IsOidc);

    public record OrganizationMailAccountRow(int Id, string Name, string EmailAddress, string Provider, bool IsEnabled,
        DateTime LastSync, long Emails, long StorageBytes);

    public class PackageViewModel
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>Storage in GB; empty = unlimited.</summary>
        [Range(0, 1_000_000)]
        public decimal? StorageQuotaGb { get; set; }

        [Range(0, 1_000_000)]
        public int? MaxMailAccounts { get; set; }

        public bool BlockArchivingOverQuota { get; set; }

        public bool IsActive { get; set; } = true;

        public static PackageViewModel From(Package p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            StorageQuotaGb = p.StorageQuotaBytes is long b ? Math.Round(b / (decimal)(1L << 30), 2) : null,
            MaxMailAccounts = p.MaxMailAccounts,
            BlockArchivingOverQuota = p.BlockArchivingOverQuota,
            IsActive = p.IsActive
        };
    }

    public class OrganizationCreateViewModel
    {
        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(64)]
        [RegularExpression("^[a-z0-9][a-z0-9-]*$")]
        public string Slug { get; set; } = string.Empty;

        public string? EmailDomains { get; set; }

        [Range(0, 100)]
        public int RetentionYears { get; set; } = 10;

        public int? PackageId { get; set; }

        public string? Notes { get; set; }

        public string? AdminUsername { get; set; }
        public string? AdminEmail { get; set; }
        public string? AdminPassword { get; set; }
    }
}
