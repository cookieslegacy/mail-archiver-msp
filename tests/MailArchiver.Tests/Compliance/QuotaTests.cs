using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Organizations;
using MailArchiver.Services.Security;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Compliance;

[Collection(TestDbFixture.CollectionName)]
public class QuotaTests : ComplianceTestBase
{
    public QuotaTests(TestDbFixture fixture) : base(fixture) { }

    private QuotaService NewQuotaService(QuotaOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(new OrganizationConnectionInterceptor())
            .Options);
        services.AddSingleton<ICredentialProtector>(NullCredentialProtector.Instance);
        return new QuotaService(services.BuildServiceProvider(), new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new QuotaOptions()), NullLogger<QuotaService>.Instance);
    }

    private static async Task<Package> CreatePackageAsync(MailArchiverDbContext ctx, long? quotaBytes, int? maxAccounts, bool block = false)
    {
        var package = new Package
        {
            Name = "p-" + Guid.NewGuid().ToString("N")[..12],
            StorageQuotaBytes = quotaBytes,
            MaxMailAccounts = maxAccounts,
            BlockArchivingOverQuota = block
        };
        ctx.Packages.Add(package);
        await ctx.SaveChangesAsync();
        return package;
    }

    [Fact]
    public async Task OriginalBytes_AreCountedOnArchive_AndReleasedWhenTheEmailIsDeleted()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 0);
        var account = await CreateAccountAsync(ctx, org);
        var first = await ArchiveWithOriginalAsync(ctx, account, "one");
        await ArchiveWithOriginalAsync(ctx, account, "two");

        var sizes = await ctx.ArchivedEmailOriginals.Where(o => o.MailAccountId == account.Id).SumAsync(o => o.SizeBytes);
        var usage = await ctx.MailAccountUsages.AsNoTracking().SingleAsync(u => u.MailAccountId == account.Id);
        Assert.Equal(sizes, usage.OriginalBytes);
        Assert.Equal(2, usage.OriginalCount);
        Assert.Equal(org.Id, usage.OrganizationId);

        // No retention obligation: the email may go, its original's bytes are purged and no longer count.
        await ctx.ArchivedEmails.Where(e => e.Id == first.Id).ExecuteDeleteAsync();
        usage = await ctx.MailAccountUsages.AsNoTracking().SingleAsync(u => u.MailAccountId == account.Id);
        Assert.Equal(1, usage.OriginalCount);
        Assert.Equal(sizes - await ctx.ArchivedEmailOriginals.Where(o => o.ArchivedEmailId == first.Id).Select(o => o.SizeBytes).SingleAsync(),
            usage.OriginalBytes);
    }

    [Fact]
    public async Task MailAccountLimit_IsEnforcedByTheDatabase_AndAnIndividualLimitOverridesThePackage()
    {
        await using var ctx = SystemContext();
        var package = await CreatePackageAsync(ctx, null, maxAccounts: 2);
        var org = await CreateOrganizationAsync(ctx);
        org.PackageId = package.Id;
        await ctx.SaveChangesAsync();

        await CreateAccountAsync(ctx, org);
        await CreateAccountAsync(ctx, org);
        var quota = NewQuotaService();
        Assert.False(await quota.CanAddMailAccountsAsync(org.Id));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => CreateAccountAsync(ctx, org));
        Assert.True(QuotaService.IsMailAccountLimitError(ex));
        ctx.ChangeTracker.Clear();

        var reloaded = await ctx.Organizations.SingleAsync(o => o.Id == org.Id);
        reloaded.MaxMailAccounts = 3;
        await ctx.SaveChangesAsync();
        Assert.True(await quota.CanAddMailAccountsAsync(org.Id));
        await CreateAccountAsync(ctx, reloaded);
        Assert.Equal(3, await ctx.MailAccounts.CountAsync(a => a.OrganizationId == org.Id));
    }

    [Fact]
    public void Evaluate_GivesTheLevels_WarnExceedAndBlockAfterTheGraceMargin()
    {
        var options = new QuotaOptions { WarnPercent = 80, BlockGracePercent = 10 };
        var warnOnly = new Organization { Id = 1, Package = new Package { Name = "S", StorageQuotaBytes = 1000 } };
        var blocking = new Organization { Id = 2, Package = new Package { Name = "B", StorageQuotaBytes = 1000, BlockArchivingOverQuota = true } };
        var unlimited = new Organization { Id = 3 };

        Assert.Equal(QuotaLevel.Ok, QuotaService.Evaluate(warnOnly, (799, 0), 1, options).Level);
        Assert.Equal(QuotaLevel.Warning, QuotaService.Evaluate(warnOnly, (800, 0), 1, options).Level);
        Assert.Equal(QuotaLevel.Exceeded, QuotaService.Evaluate(warnOnly, (5000, 0), 1, options).Level);
        Assert.Equal(QuotaLevel.Exceeded, QuotaService.Evaluate(blocking, (1099, 0), 1, options).Level);
        Assert.Equal(QuotaLevel.Blocked, QuotaService.Evaluate(blocking, (1100, 0), 1, options).Level);
        Assert.Equal(QuotaLevel.Unlimited, QuotaService.Evaluate(unlimited, (long.MaxValue / 2, 0), 1, options).Level);

        // An individual quota replaces the package's.
        warnOnly.StorageQuotaBytes = 10_000;
        Assert.Equal(QuotaLevel.Ok, QuotaService.Evaluate(warnOnly, (5000, 0), 1, options).Level);
    }

    [Fact]
    public async Task Archiving_IsPaused_OnlyForBlockingPackagesOverQuota()
    {
        await using var ctx = SystemContext();
        var blocking = await CreatePackageAsync(ctx, quotaBytes: 100, maxAccounts: null, block: true);
        var warning = await CreatePackageAsync(ctx, quotaBytes: 100, maxAccounts: null, block: false);
        var full = await CreateOrganizationAsync(ctx);
        var warned = await CreateOrganizationAsync(ctx);
        full.PackageId = blocking.Id;
        warned.PackageId = warning.Id;
        await ctx.SaveChangesAsync();

        var fullAccount = await CreateAccountAsync(ctx, full);
        var warnedAccount = await CreateAccountAsync(ctx, warned);
        await ArchiveWithOriginalAsync(ctx, fullAccount, "bigger than one hundred bytes");
        await ArchiveWithOriginalAsync(ctx, warnedAccount, "bigger than one hundred bytes");

        var quota = NewQuotaService();
        Assert.Equal(QuotaLevel.Blocked, (await quota.GetStatusAsync(full.Id)).Level);
        Assert.True(await quota.IsArchivingBlockedAsync(fullAccount.Id));
        Assert.Equal(QuotaLevel.Exceeded, (await quota.GetStatusAsync(warned.Id)).Level);
        Assert.False(await quota.IsArchivingBlockedAsync(warnedAccount.Id));
    }

    [Fact]
    public async Task Usage_IsVisibleOnlyInsideItsOrganization()
    {
        int accountA;
        int orgA, orgB;
        await using (var ctx = SystemContext())
        {
            var a = await CreateOrganizationAsync(ctx);
            var b = await CreateOrganizationAsync(ctx);
            orgA = a.Id;
            orgB = b.Id;
            var account = await CreateAccountAsync(ctx, a);
            accountA = account.Id;
            await ArchiveWithOriginalAsync(ctx, account, "usage");
        }

        await using (var own = ContextFor(orgA))
        {
            var bytes = await StorageUsage.ForAccountsAsync(own, new[] { accountA });
            Assert.True(bytes[accountA] > 0);
            Assert.True((await StorageUsage.ForOrganizationsAsync(own)).ContainsKey(orgA));
        }

        await using (var other = ContextFor(orgB))
        {
            var bytes = await StorageUsage.ForAccountsAsync(other, new[] { accountA });
            Assert.Equal(0, bytes[accountA]);
            var perOrganization = await StorageUsage.ForOrganizationsAsync(other);
            Assert.False(perOrganization.ContainsKey(orgA));
            Assert.False(await other.MailAccountUsages.AnyAsync());
        }
    }

    [Fact]
    public async Task Snapshot_WritesTodaysUsageRow_AndUpdatesItLater()
    {
        int orgId;
        await using (var ctx = SystemContext())
        {
            var package = await CreatePackageAsync(ctx, quotaBytes: 1L << 30, maxAccounts: 5);
            var org = await CreateOrganizationAsync(ctx);
            org.PackageId = package.Id;
            await ctx.SaveChangesAsync();
            orgId = org.Id;
            var account = await CreateAccountAsync(ctx, org);
            await ArchiveWithOriginalAsync(ctx, account, "snap");
        }

        var quota = NewQuotaService();
        await quota.SnapshotAsync();
        await using (var ctx = SystemContext())
        {
            var row = await ctx.OrganizationUsageSnapshots.AsNoTracking()
                .SingleAsync(u => u.OrganizationId == orgId && u.Day == DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.Equal(1, row.MailAccounts);
            Assert.Equal(1, row.Emails);
            Assert.Equal(1L << 30, row.StorageQuotaBytes);
            Assert.True(row.StorageBytes > 0);

            var account = await ctx.MailAccounts.FirstAsync(a => a.OrganizationId == orgId);
            await ArchiveWithOriginalAsync(ctx, account, "snap 2");
        }

        await quota.SnapshotAsync();
        await using (var ctx = SystemContext())
        {
            var row = await ctx.OrganizationUsageSnapshots.AsNoTracking()
                .SingleAsync(u => u.OrganizationId == orgId && u.Day == DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.Equal(2, row.Emails);
        }
    }

    [Theory]
    [InlineData("MailAccounts", "Index", true)]
    [InlineData("MailAccounts", "Create", true)]
    [InlineData("MailAccounts", "ImportMBox", true)]
    [InlineData("MailAccounts", "Export", false)]
    [InlineData("MailAccounts", "DownloadExport", false)]
    [InlineData("MailAccounts", "MoveAllEmails", false)]
    [InlineData("Organizations", "Edit", true)]
    [InlineData("Emails", "Index", false)]
    [InlineData("Emails", "Details", false)]
    [InlineData("Home", "Index", false)]
    [InlineData("Compliance", "Original", false)]
    [InlineData("Users", "Index", false)]
    [InlineData("Logs", "Index", false)]
    [InlineData("ApiKeys", "Index", false)]
    public void ManagementAccess_OpensAdministrationOnly(string controller, string action, bool open)
    {
        Assert.Equal(open, ProviderManagementFilter.IsOpen(controller, action));
    }
}
