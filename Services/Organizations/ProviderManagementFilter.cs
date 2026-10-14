using MailArchiver.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MailArchiver.Services.Organizations
{
    /// <summary>
    /// Confines the provider's management access to administration. While a provider admin
    /// manages an organization (no approval by the organization needed), only the pages listed
    /// here are open: mail accounts (create, edit, sync, import) and the organizations pages.
    /// Everything that shows or hands out email content - emails, search, exports, dashboard,
    /// audit log, users, compliance - is closed. Every change is written into the managed
    /// organization's audit log.
    /// </summary>
    public class ProviderManagementFilter : IAsyncActionFilter
    {
        // Controller -> actions that stay closed; null = the whole controller is open.
        private static readonly Dictionary<string, HashSet<string>?> Open = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MailAccounts"] = new(StringComparer.OrdinalIgnoreCase)
            {
                // Exports and moves hand out or rearrange content.
                "Export", "ExportStatus", "DownloadExport", "CancelExport", "Offload", "MoveAllEmails"
            },
            ["Organizations"] = null,
            ["Auth"] = null,
            ["TwoFactor"] = null,
            ["Localization"] = null,
            // The dashboard shows recent emails; the rest of Home is static.
            ["Home"] = new(StringComparer.OrdinalIgnoreCase) { "Index", "ChartData" },
        };

        private readonly IOrganizationContext _scope;
        private readonly IServiceProvider _services;
        private readonly IAuthenticationService _auth;
        private readonly ILogger<ProviderManagementFilter> _logger;

        public ProviderManagementFilter(IOrganizationContext scope, IServiceProvider services,
            IAuthenticationService auth, ILogger<ProviderManagementFilter> logger)
        {
            _scope = scope;
            _services = services;
            _auth = auth;
            _logger = logger;
        }

        public static bool IsOpen(string? controller, string? action) =>
            controller != null && Open.TryGetValue(controller, out var closed)
            && (closed == null || action == null || !closed.Contains(action));

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!_scope.IsProviderManagementAccess)
            {
                await next();
                return;
            }

            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
            var controller = descriptor?.ControllerName;
            var action = descriptor?.ActionName;

            if (!IsOpen(controller, action))
            {
                _logger.LogWarning("Provider management access: {Controller}/{Action} is closed", controller, action);
                var request = context.HttpContext.Request;
                var isPageRequest = HttpMethods.IsGet(request.Method)
                    && !string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
                if (isPageRequest)
                {
                    var tempData = context.HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>()?.GetTempData(context.HttpContext);
                    if (tempData != null)
                        tempData["WarningMessage"] = ManagementClosedMessage(context.HttpContext);
                    context.Result = new RedirectToActionResult("Index", "MailAccounts", null);
                }
                else
                {
                    context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                }
                return;
            }

            var executed = await next();

            if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && _scope.OrganizationId is int organizationId
                && !string.Equals(controller, "Organizations", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAuditAsync(organizationId, context, controller, action, executed.Exception == null || executed.ExceptionHandled);
            }
        }

        private async Task WriteAuditAsync(int organizationId, ActionExecutingContext context, string? controller, string? action, bool succeeded)
        {
            try
            {
                var target = context.ActionArguments.TryGetValue("id", out var id) ? $" id={id}" : string.Empty;
                await using var system = OrganizationService.CreateSystemContext(_services);
                system.AccessLogs.Add(new AccessLog
                {
                    Username = _auth.GetCurrentUserDisplayName(context.HttpContext) ?? "unknown",
                    Type = AccessLogType.Organization,
                    Timestamp = DateTime.UtcNow,
                    OrganizationId = organizationId,
                    SearchParameters = $"Provider management: {controller}/{action}{target}" + (succeeded ? string.Empty : " (failed)")
                });
                await system.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write the audit entry of a provider management action");
            }
        }

        private static string ManagementClosedMessage(HttpContext http)
        {
            var localizer = http.RequestServices.GetService<Microsoft.Extensions.Localization.IStringLocalizer<ComplianceResource>>();
            return localizer?["ManagementPageClosed"].Value
                ?? "This page shows email content and is not available in management mode.";
        }
    }
}
