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
        private readonly IUserService _userService;
        private readonly IAuthenticationService _authService;
        private readonly IServiceProvider _services;
        private readonly IStringLocalizer<ComplianceResource> _l;
        private readonly ILogger<OrganizationsController> _logger;

        public OrganizationsController(IOrganizationService organizations, IUserService userService,
            IAuthenticationService authService, IServiceProvider services,
            IStringLocalizer<ComplianceResource> localizer, ILogger<OrganizationsController> logger)
        {
            _organizations = organizations;
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
            return View(await _organizations.GetOverviewAsync());
        }

        public IActionResult Create() => View(new OrganizationCreateViewModel());

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
                return View(model);

            var organization = await _organizations.CreateAsync(new Organization
            {
                Name = model.Name.Trim(),
                Slug = model.Slug,
                EmailDomains = model.EmailDomains ?? string.Empty,
                RetentionYears = model.RetentionYears,
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

        public string? Notes { get; set; }

        public string? AdminUsername { get; set; }
        public string? AdminEmail { get; set; }
        public string? AdminPassword { get; set; }
    }
}
