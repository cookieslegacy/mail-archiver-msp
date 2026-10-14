using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Organizations
{
    public class QuotaOptions
    {
        public const string SectionName = "Quota";

        /// <summary>From this share of the storage quota on admins see a warning.</summary>
        public int WarnPercent { get; set; } = 80;

        /// <summary>
        /// Packages that block archiving stop only once the quota is exceeded by this margin,
        /// so a short overrun does not interrupt archiving at once.
        /// </summary>
        public int BlockGracePercent { get; set; } = 10;

        /// <summary>How often today's usage row is written (billing, trend).</summary>
        public int SnapshotIntervalMinutes { get; set; } = 60;
    }

    public enum QuotaLevel
    {
        Unlimited,
        Ok,
        Warning,
        Exceeded,
        Blocked
    }

    /// <summary>Storage and mail account usage of one organization against its package.</summary>
    public record QuotaStatus(
        int OrganizationId,
        string? PackageName,
        long UsedBytes,
        long OriginalBytes,
        long? QuotaBytes,
        int MailAccounts,
        int? MaxMailAccounts,
        bool BlockOverQuota,
        QuotaLevel Level)
    {
        public double? Percent => QuotaBytes is > 0 ? 100.0 * UsedBytes / QuotaBytes.Value : null;
        public bool MailAccountLimitReached => MaxMailAccounts.HasValue && MailAccounts >= MaxMailAccounts.Value;
    }

    /// <summary>
    /// Storage used by mail accounts and organizations. The figure is what the archive occupies:
    /// the searchable copy of each email with its attachments (refreshed after every sync and
    /// nightly) plus the stored originals (kept up to date by the database).
    /// </summary>
    public static class StorageUsage
    {
        /// <summary>Bytes per mail account, for the accounts visible to the context.</summary>
        public static async Task<Dictionary<int, long>> ForAccountsAsync(MailArchiverDbContext context, IReadOnlyCollection<int> accountIds)
        {
            var ids = accountIds.Distinct().ToList();
            var result = ids.ToDictionary(id => id, _ => 0L);
            if (ids.Count == 0)
                return result;

            var cached = await context.AccountStorageCaches.AsNoTracking()
                .Where(c => ids.Contains(c.MailAccountId))
                .Select(c => new { c.MailAccountId, c.TotalBytes })
                .ToListAsync();
            foreach (var c in cached)
                result[c.MailAccountId] += c.TotalBytes;

            var originals = await context.MailAccountUsages.AsNoTracking()
                .Where(u => ids.Contains(u.MailAccountId))
                .Select(u => new { u.MailAccountId, u.OriginalBytes })
                .ToListAsync();
            foreach (var o in originals)
                result[o.MailAccountId] += o.OriginalBytes;

            return result;
        }

        /// <summary>Total bytes and bytes of originals per organization visible to the context.</summary>
        public static async Task<Dictionary<int, (long Total, long Originals)>> ForOrganizationsAsync(MailArchiverDbContext context)
        {
            var cached = await context.AccountStorageCaches.AsNoTracking()
                .GroupBy(c => c.MailAccount.OrganizationId)
                .Select(g => new { g.Key, Bytes = g.Sum(c => c.TotalBytes) })
                .ToDictionaryAsync(x => x.Key, x => x.Bytes);
            var originals = await context.MailAccountUsages.AsNoTracking()
                .GroupBy(u => u.OrganizationId)
                .Select(g => new { g.Key, Bytes = g.Sum(u => u.OriginalBytes) })
                .ToDictionaryAsync(x => x.Key, x => x.Bytes);

            return cached.Keys.Union(originals.Keys).ToDictionary(
                id => id,
                id => (cached.GetValueOrDefault(id) + originals.GetValueOrDefault(id), originals.GetValueOrDefault(id)));
        }
    }

    public interface IQuotaService
    {
        /// <summary>Status of every organization (provider overview).</summary>
        Task<Dictionary<int, QuotaStatus>> GetAllAsync(CancellationToken ct = default);

        Task<QuotaStatus> GetStatusAsync(int organizationId, CancellationToken ct = default);

        /// <summary>Whether new mail must not be fetched for this account (package blocks, quota used up).</summary>
        Task<bool> IsArchivingBlockedAsync(int mailAccountId, CancellationToken ct = default);

        /// <summary>Whether the organization may get <paramref name="count"/> more mail accounts.</summary>
        Task<bool> CanAddMailAccountsAsync(int organizationId, int count = 1, CancellationToken ct = default);

        /// <summary>Writes today's usage row of every organization.</summary>
        Task SnapshotAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Evaluates packages and quotas. Always reads through a system context: the figures concern
    /// the organization as a whole, whoever asks (a user only sees the accounts assigned to them).
    /// Callers decide what to show to whom.
    /// </summary>
    public class QuotaService : IQuotaService
    {
        private static readonly TimeSpan BlockCacheTime = TimeSpan.FromSeconds(60);

        private readonly IServiceProvider _services;
        private readonly IMemoryCache _cache;
        private readonly QuotaOptions _options;
        private readonly ILogger<QuotaService> _logger;

        public QuotaService(IServiceProvider services, IMemoryCache cache, IOptions<QuotaOptions> options, ILogger<QuotaService> logger)
        {
            _services = services;
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<Dictionary<int, QuotaStatus>> GetAllAsync(CancellationToken ct = default)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            var organizations = await system.Organizations.AsNoTracking().Include(o => o.Package).ToListAsync(ct);
            var storage = await StorageUsage.ForOrganizationsAsync(system);
            var accounts = await system.MailAccounts
                .GroupBy(a => a.OrganizationId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            return organizations.ToDictionary(o => o.Id,
                o => Evaluate(o, storage.GetValueOrDefault(o.Id), accounts.GetValueOrDefault(o.Id), _options));
        }

        public async Task<QuotaStatus> GetStatusAsync(int organizationId, CancellationToken ct = default)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            var organization = await system.Organizations.AsNoTracking().Include(o => o.Package)
                .FirstOrDefaultAsync(o => o.Id == organizationId, ct);
            if (organization == null)
                return new QuotaStatus(organizationId, null, 0, 0, null, 0, null, false, QuotaLevel.Unlimited);

            var accountIds = await system.MailAccounts.Where(a => a.OrganizationId == organizationId)
                .Select(a => a.Id).ToListAsync(ct);
            var bytes = await StorageUsage.ForAccountsAsync(system, accountIds);
            var originals = await system.MailAccountUsages.Where(u => u.OrganizationId == organizationId)
                .SumAsync(u => (long?)u.OriginalBytes, ct) ?? 0;

            return Evaluate(organization, (bytes.Values.Sum(), originals), accountIds.Count, _options);
        }

        public static QuotaStatus Evaluate(Organization organization, (long Total, long Originals) storage, int mailAccounts,
            QuotaOptions options)
        {
            var quota = organization.EffectiveStorageQuotaBytes;
            var block = organization.BlockArchivingOverQuota;
            QuotaLevel level;
            if (quota is not > 0)
                level = QuotaLevel.Unlimited;
            else if (block && storage.Total >= quota.Value + quota.Value * options.BlockGracePercent / 100)
                level = QuotaLevel.Blocked;
            else if (storage.Total >= quota.Value)
                level = QuotaLevel.Exceeded;
            else if (storage.Total >= quota.Value * options.WarnPercent / 100)
                level = QuotaLevel.Warning;
            else
                level = QuotaLevel.Ok;

            return new QuotaStatus(organization.Id, organization.Package?.Name, storage.Total, storage.Originals,
                quota, mailAccounts, organization.EffectiveMaxMailAccounts, block, level);
        }

        public async Task<bool> IsArchivingBlockedAsync(int mailAccountId, CancellationToken ct = default)
        {
            int organizationId;
            await using (var system = OrganizationService.CreateSystemContext(_services))
            {
                var org = await system.MailAccounts.Where(a => a.Id == mailAccountId)
                    .Select(a => (int?)a.OrganizationId).FirstOrDefaultAsync(ct);
                if (org == null)
                    return false;
                organizationId = org.Value;
            }

            var level = await _cache.GetOrCreateAsync($"quota-level-{organizationId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = BlockCacheTime;
                entry.Size = 1;
                return (await GetStatusAsync(organizationId, ct)).Level;
            });
            if (level == QuotaLevel.Blocked)
                _logger.LogWarning("Archiving of mail account {AccountId} is paused: organization {OrganizationId} has used up its storage quota",
                    mailAccountId, organizationId);
            return level == QuotaLevel.Blocked;
        }

        public async Task<bool> CanAddMailAccountsAsync(int organizationId, int count = 1, CancellationToken ct = default)
        {
            await using var system = OrganizationService.CreateSystemContext(_services);
            var organization = await system.Organizations.AsNoTracking().Include(o => o.Package)
                .FirstOrDefaultAsync(o => o.Id == organizationId, ct);
            var max = organization?.EffectiveMaxMailAccounts;
            if (max == null)
                return true;
            var existing = await system.MailAccounts.CountAsync(a => a.OrganizationId == organizationId, ct);
            return existing + count <= max.Value;
        }

        public async Task SnapshotAsync(CancellationToken ct = default)
        {
            var statuses = await GetAllAsync(ct);
            await using var system = OrganizationService.CreateSystemContext(_services);
            var emails = await system.ArchivedEmails
                .GroupBy(e => e.OrganizationId)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            foreach (var status in statuses.Values)
            {
                await system.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO mail_archiver.""OrganizationUsageSnapshots""
                        (""OrganizationId"", ""Day"", ""StorageBytes"", ""OriginalBytes"", ""MailAccounts"", ""Emails"", ""StorageQuotaBytes"", ""PackageName"")
                    VALUES ({status.OrganizationId}, {today}, {status.UsedBytes}, {status.OriginalBytes}, {status.MailAccounts},
                            {emails.GetValueOrDefault(status.OrganizationId)}, {status.QuotaBytes}, {status.PackageName})
                    ON CONFLICT (""OrganizationId"", ""Day"") DO UPDATE SET
                        ""StorageBytes"" = EXCLUDED.""StorageBytes"",
                        ""OriginalBytes"" = EXCLUDED.""OriginalBytes"",
                        ""MailAccounts"" = EXCLUDED.""MailAccounts"",
                        ""Emails"" = EXCLUDED.""Emails"",
                        ""StorageQuotaBytes"" = EXCLUDED.""StorageQuotaBytes"",
                        ""PackageName"" = EXCLUDED.""PackageName""", ct);
            }
        }

        /// <summary>Message for the database's refusal of a mail account beyond the limit, or null.</summary>
        public static bool IsMailAccountLimitError(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
                if (e is Npgsql.PostgresException { SqlState: "MA004" })
                    return true;
            return false;
        }
    }

    /// <summary>Writes today's usage row of every organization periodically.</summary>
    public class UsageSnapshotService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly QuotaOptions _options;
        private readonly ILogger<UsageSnapshotService> _logger;

        public UsageSnapshotService(IServiceScopeFactory scopeFactory, IOptions<QuotaOptions> options, ILogger<UsageSnapshotService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<IQuotaService>().SnapshotAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Writing the usage snapshot failed");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _options.SnapshotIntervalMinutes)), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

namespace MailArchiver.Services.Organizations
{
    /// <summary>One-line quota checks for the controllers of the original code base.</summary>
    public static class QuotaGuard
    {
        /// <summary>A message when new mail must not be archived into the account, else null.</summary>
        public static async Task<string?> ArchivingBlockedMessageAsync(HttpContext http, int mailAccountId)
        {
            var quota = http.RequestServices.GetRequiredService<IQuotaService>();
            if (!await quota.IsArchivingBlockedAsync(mailAccountId))
                return null;
            return Localizer(http)?["ArchivingPausedQuota"].Value
                ?? "The storage of your package is used up; archiving is paused until the package is extended.";
        }

        /// <summary>A message when the current organization may not get <paramref name="count"/> more mail accounts, else null.</summary>
        public static async Task<string?> MailAccountLimitMessageAsync(HttpContext http, int count = 1)
        {
            var scope = http.RequestServices.GetRequiredService<IOrganizationContext>();
            if (scope.OrganizationId is not int organizationId)
                return null;
            var quota = http.RequestServices.GetRequiredService<IQuotaService>();
            if (await quota.CanAddMailAccountsAsync(organizationId, count))
                return null;
            return MailAccountLimitMessage(http);
        }

        public static string MailAccountLimitMessage(HttpContext http) =>
            Localizer(http)?["MailAccountLimitReached"].Value
                ?? "Your package does not include more mail accounts.";

        private static Microsoft.Extensions.Localization.IStringLocalizer<ComplianceResource>? Localizer(HttpContext http) =>
            http.RequestServices.GetService<Microsoft.Extensions.Localization.IStringLocalizer<ComplianceResource>>();
    }
}
