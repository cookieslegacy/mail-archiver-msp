using MailArchiver.Services.Organizations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MailArchiver.Attributes
{
    /// <summary>Base for filters that require a signed in user with a given role claim.</summary>
    public abstract class RoleRequiredAttribute : ActionFilterAttribute
    {
        protected abstract bool IsAllowed(HttpContext context, MailArchiver.Services.IAuthenticationService authService);

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var authService = context.HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            if (authService == null || !authService.IsAuthenticated(context.HttpContext))
            {
                var returnUrl = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
                context.Result = new RedirectToActionResult("Login", "Auth", new { returnUrl });
                return;
            }

            if (!IsAllowed(context.HttpContext, authService))
            {
                var logger = context.HttpContext.RequestServices.GetService<ILogger<RoleRequiredAttribute>>();
                logger?.LogWarning("User {UserId} was denied {Filter}",
                    authService.GetCurrentUserId(context.HttpContext), GetType().Name);
                context.Result = new RedirectToActionResult("AccessDenied", "Auth", null);
                return;
            }

            base.OnActionExecuting(context);
        }
    }

    /// <summary>Only provider admins, who manage organizations.</summary>
    public class ProviderAdminRequiredAttribute : RoleRequiredAttribute
    {
        protected override bool IsAllowed(HttpContext context, MailArchiver.Services.IAuthenticationService authService) =>
            context.User.IsInRole(OrganizationClaims.ProviderAdminRole);
    }

    /// <summary>Admins and auditors of the current organization.</summary>
    public class AdminOrAuditorRequiredAttribute : RoleRequiredAttribute
    {
        protected override bool IsAllowed(HttpContext context, MailArchiver.Services.IAuthenticationService authService) =>
            authService.IsCurrentUserAdmin(context) || context.User.IsInRole(OrganizationClaims.AuditorRole);
    }
}
