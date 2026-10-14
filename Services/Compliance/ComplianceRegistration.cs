using MailArchiver.Data;
using MailArchiver.Services.Compliance;
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
            services.Configure<QuotaOptions>(builder.Configuration.GetSection(QuotaOptions.SectionName));
            services.AddMemoryCache();
            services.AddScoped<IQuotaService, QuotaService>();
            services.AddHostedService<UsageSnapshotService>();
            services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add<ProviderManagementFilter>());

            services.Configure<ArchiveStorageOptions>(builder.Configuration.GetSection(ArchiveStorageOptions.SectionName));
            services.Configure<ComplianceOptions>(builder.Configuration.GetSection(ComplianceOptions.SectionName));

            var storage = builder.Configuration.GetSection(ArchiveStorageOptions.SectionName).Get<ArchiveStorageOptions>()
                ?? new ArchiveStorageOptions();
            if (storage.UsesWorm)
                services.AddSingleton<IWormStore, S3WormStore>();
            else
                services.AddSingleton<IWormStore>(DisabledWormStore.Instance);

            services.AddSingleton<IOriginalArchiveService, OriginalArchiveService>();
            services.AddScoped<IntegrityVerificationService>();
            services.AddScoped<AnchorService>();
            services.AddScoped<RetentionService>();
            services.AddSingleton<IntegrityCheckQueue>();
            services.AddHttpClient(nameof(AnchorService), client => client.Timeout = TimeSpan.FromSeconds(30));
            services.AddHostedService<ComplianceBackgroundService>();

            return builder;
        }
    }
}
