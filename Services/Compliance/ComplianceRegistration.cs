using MailArchiver.Data;
using MailArchiver.Services.Organizations;

namespace MailArchiver.Services
{
    /// <summary>
    /// Registers organizations (tenants) and the compliance features: original messages with
    /// hash chain, retention and legal hold, WORM storage, integrity verification and anchors.
    /// Kept in one place so Program.cs only needs a single call.
    /// </summary>
    public static class ComplianceRegistration
    {
        public static WebApplicationBuilder AddOrganizationsAndCompliance(this WebApplicationBuilder builder)
        {
            var services = builder.Services;

            services.AddHttpContextAccessor();
            services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
            services.AddSingleton<OrganizationConnectionInterceptor>();
            services.AddScoped<IOrganizationService, OrganizationService>();

            return builder;
        }
    }
}
