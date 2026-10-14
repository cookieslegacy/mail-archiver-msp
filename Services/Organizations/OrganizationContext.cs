using System.Security.Claims;
using MailArchiver.Models;

namespace MailArchiver.Services.Organizations
{
    /// <summary>
    /// Tells the data layer which organization (tenant) the current unit of work acts for.
    /// The DbContext turns this into global query filters and into the PostgreSQL row level
    /// security setting, so every query is confined to one organization.
    /// </summary>
    public interface IOrganizationContext
    {
        /// <summary>
        /// Background work without a signed in user (sync, maintenance, jobs, tests). Sees all
        /// organizations; such code works on records it got by id and inherits their organization.
        /// </summary>
        bool IsSystem { get; }

        /// <summary>A request that is not (yet) authenticated, e.g. the login page.</summary>
        bool IsAnonymous { get; }

        /// <summary>The signed in user manages organizations (provider staff).</summary>
        bool IsProviderAdmin { get; }

        /// <summary>
        /// The organization whose data the current user may see, or null when there is none
        /// (then all organization data is hidden).
        /// </summary>
        int? OrganizationId { get; }

        /// <summary>True while a provider admin works inside another organization for support.</summary>
        bool IsProviderSupportAccess { get; }

        /// <summary>
        /// True while a provider admin manages another organization's mail accounts. Unlike
        /// support access this needs no approval, and it is confined to administration: the
        /// <see cref="ProviderManagementFilter"/> keeps every page that shows email content closed.
        /// </summary>
        bool IsProviderManagementAccess { get; }

        /// <summary>
        /// Admins and auditors see emails whose processing is restricted (Art. 18 GDPR);
        /// everyone else does not.
        /// </summary>
        bool CanSeeRestricted { get; }
    }

    /// <summary>Claim and session keys shared by sign-in and the organization context.</summary>
    public static class OrganizationClaims
    {
        public const string OrganizationId = "OrganizationId";
        public const string ProviderAdminRole = "ProviderAdmin";
        public const string AuditorRole = "Auditor";

        /// <summary>Session key holding the organization a provider admin switched into.</summary>
        public const string SupportOrganizationSessionKey = "SupportOrganizationId";

        /// <summary>Session key holding the organization a provider admin manages (no content).</summary>
        public const string ManagementOrganizationSessionKey = "ManagementOrganizationId";

        public static IEnumerable<Claim> For(User user)
        {
            if (user.OrganizationId.HasValue)
                yield return new Claim(OrganizationId, user.OrganizationId.Value.ToString());
            if (user.IsProviderAdmin)
                yield return new Claim(ClaimTypes.Role, ProviderAdminRole);
            if (user.IsAuditor)
                yield return new Claim(ClaimTypes.Role, AuditorRole);
        }
    }

    /// <summary>
    /// Resolves the organization from the current HTTP request. Evaluated on every access
    /// (not cached) because the DbContext of a request may be created before authentication
    /// has run, e.g. for API key requests.
    /// </summary>
    public class HttpOrganizationContext : IOrganizationContext
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpOrganizationContext(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        private HttpContext? Http => _httpContextAccessor.HttpContext;

        private ClaimsPrincipal? AuthenticatedUser
        {
            get
            {
                var user = Http?.User;
                return user?.Identity?.IsAuthenticated == true ? user : null;
            }
        }

        public bool IsSystem => Http == null;

        public bool IsAnonymous => Http != null && AuthenticatedUser == null;

        public bool IsProviderAdmin => AuthenticatedUser?.IsInRole(OrganizationClaims.ProviderAdminRole) == true;

        public bool IsProviderSupportAccess => SupportOrganizationId.HasValue;

        public bool IsProviderManagementAccess => !SupportOrganizationId.HasValue && ManagementOrganizationId.HasValue;

        public bool CanSeeRestricted =>
            AuthenticatedUser is { } user && (user.IsInRole("Admin") || user.IsInRole(OrganizationClaims.AuditorRole));

        public int? OrganizationId
        {
            get
            {
                var http = Http;
                var user = AuthenticatedUser;
                if (http == null || user == null)
                    return null;

                // Resolved once per request and kept in Items: work that an action starts in
                // the background (manual sync, imports) may still run after the response has
                // been sent, when the session is gone and only the claims remain. For a
                // provider admin in support or management mode that would silently switch
                // the organization. Items live until the request is disposed; after that the
                // accessor returns null and the work continues in system scope.
                if (http.Items.TryGetValue(ItemsKey, out var resolved))
                    return (int?)resolved;

                var organizationId = SupportOrganizationId ?? ManagementOrganizationId;
                if (!organizationId.HasValue)
                {
                    var claim = user.FindFirst(OrganizationClaims.OrganizationId)?.Value;
                    organizationId = int.TryParse(claim, out var id) ? id : null;
                }
                http.Items[ItemsKey] = organizationId;
                return organizationId;
            }
        }

        private const string ItemsKey = "MailArchiver.OrganizationId";

        private int? SupportOrganizationId => SessionOrganization(OrganizationClaims.SupportOrganizationSessionKey);

        private int? ManagementOrganizationId => SessionOrganization(OrganizationClaims.ManagementOrganizationSessionKey);

        private int? SessionOrganization(string key)
        {
            if (!IsProviderAdmin)
                return null;
            try
            {
                // Session is only available for cookie requests; API requests have none.
                var http = Http;
                if (http?.Features.Get<Microsoft.AspNetCore.Http.Features.ISessionFeature>() == null)
                    return null;
                return http.Session.GetInt32(key);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Organization context for code that runs outside any request.</summary>
    public sealed class SystemOrganizationContext : IOrganizationContext
    {
        public static readonly SystemOrganizationContext Instance = new();

        public bool IsSystem => true;
        public bool IsAnonymous => false;
        public bool IsProviderAdmin => false;
        public int? OrganizationId => null;
        public bool IsProviderSupportAccess => false;
        public bool IsProviderManagementAccess => false;
        public bool CanSeeRestricted => true;
    }

    /// <summary>A fixed organization, for tests and for jobs acting on behalf of one organization.</summary>
    public sealed class FixedOrganizationContext : IOrganizationContext
    {
        public FixedOrganizationContext(int? organizationId, bool isProviderAdmin = false, bool canSeeRestricted = true)
        {
            OrganizationId = organizationId;
            IsProviderAdmin = isProviderAdmin;
            CanSeeRestricted = canSeeRestricted;
        }

        public bool CanSeeRestricted { get; }

        public bool IsSystem => false;
        public bool IsAnonymous => false;
        public bool IsProviderAdmin { get; }
        public int? OrganizationId { get; }
        public bool IsProviderSupportAccess => false;
        public bool IsProviderManagementAccess => false;
    }
}

namespace MailArchiver.Services.Organizations
{
    /// <summary>Keys of the dashboard statistics caches.</summary>
    public static class DashboardScope
    {
        /// <summary>All accounts of the instance (system scope).</summary>
        public const string SystemKey = "admin";

        /// <summary>All accounts the current scope may see: the instance, or one organization.</summary>
        public static string AdminKey(IOrganizationContext scope) =>
            scope.IsSystem ? SystemKey : OrganizationKey(scope.OrganizationId ?? -1);

        public static string OrganizationKey(int organizationId) => $"admin-org{organizationId}";
    }
}
