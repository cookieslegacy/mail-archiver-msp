using MailArchiver.Models;
using MailArchiver.Services.Compliance;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.Compliance;

[Collection(TestDbFixture.CollectionName)]
public class RetentionTests : ComplianceTestBase
{
    public RetentionTests(TestDbFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RetainUntil_IsEndOfCalendarYearPlusRetentionYears()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 10);
        var account = await CreateAccountAsync(ctx, org);
        var shortAccount = await CreateAccountAsync(ctx, org, retentionYears: 6);

        var email = NewEmail(account, sent: new DateTime(2026, 3, 1, 10, 0, 0));
        var handelsbrief = NewEmail(shortAccount, sent: new DateTime(2025, 12, 31, 23, 0, 0));
        ctx.ArchivedEmails.AddRange(email, handelsbrief);
        await ctx.SaveChangesAsync();

        var expected10 = new DateTime(2037, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected6 = new DateTime(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected10, email.RetainUntil!.Value.ToUniversalTime());
        Assert.Equal(expected6, handelsbrief.RetainUntil!.Value.ToUniversalTime());
        Assert.Equal(expected10, RetentionCalculator.RetentionEnd(email.SentDate, email.ReceivedDate, 10));
    }

    [Fact]
    public async Task EmailUnderRetention_CannotBeDeleted_ByAnyPath()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 10);
        var account = await CreateAccountAsync(ctx, org);
        var email = await ArchiveWithOriginalAsync(ctx, account, "kept");

        var direct = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmails.Where(e => e.Id == email.Id).ExecuteDeleteAsync());
        Assert.Equal("MA001", direct.SqlState);

        // Deleting the whole account cascades to the email and is refused as well.
        var cascade = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync());
        Assert.Equal("MA001", cascade.SqlState);

        // Inside a transaction that is rolled back, so a broken guard cannot wipe the test database.
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            var truncate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ctx.Database.ExecuteSqlRawAsync("TRUNCATE mail_archiver.\"ArchivedEmails\" CASCADE"));
            Assert.Contains("not allowed", truncate.MessageText);
            await tx.RollbackAsync();
        }

        Assert.True(await ctx.ArchivedEmails.AnyAsync(e => e.Id == email.Id));
    }

    [Fact]
    public async Task Retention_CanBeExtended_ButNotShortened()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 10);
        var email = await ArchiveWithOriginalAsync(ctx, await CreateAccountAsync(ctx, org), "extend");
        var retainUntil = email.RetainUntil!.Value.ToUniversalTime();

        var shorten = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmails.Where(e => e.Id == email.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.RetainUntil, retainUntil.AddYears(-1))));
        Assert.Equal("MA003", shorten.SqlState);

        await ctx.ArchivedEmails.Where(e => e.Id == email.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.RetainUntil, retainUntil.AddYears(5)));

        // The original follows, so its WORM retention can be extended too.
        var original = await ctx.ArchivedEmailOriginals.AsNoTracking().SingleAsync(o => o.ArchivedEmailId == email.Id);
        Assert.Equal(retainUntil.AddYears(5), original.RetainUntil!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("email")]
    [InlineData("account")]
    [InlineData("organization")]
    public async Task LegalHold_BlocksDeletion_EvenAfterRetention(string level)
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 0);
        var account = await CreateAccountAsync(ctx, org);
        var email = await ArchiveWithOriginalAsync(ctx, account, "held");

        switch (level)
        {
            case "email":
                await ctx.ArchivedEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.LegalHold, true));
                break;
            case "account":
                await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.LegalHold, true));
                break;
            default:
                await ctx.Organizations.Where(o => o.Id == org.Id).ExecuteUpdateAsync(u => u.SetProperty(o => o.LegalHold, true));
                break;
        }

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmails.Where(e => e.Id == email.Id).ExecuteDeleteAsync());
        Assert.Equal("MA002", ex.SqlState);
        Assert.Equal(0, await Retention(ctx).PurgeExpiredAsync(org.Id, "test"));
    }

    [Fact]
    public async Task ExpiredEmails_ArePurged_TheirOriginalsKeepTheProof()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 0);
        var account = await CreateAccountAsync(ctx, org);
        var expired = await ArchiveWithOriginalAsync(ctx, account, "expired");
        var kept = await ArchiveWithOriginalAsync(ctx, account, "kept");
        await ctx.ArchivedEmails.Where(e => e.Id == kept.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.LegalHold, true));

        var deleted = await Retention(ctx).PurgeExpiredAsync(org.Id, "test");

        Assert.Equal(1, deleted);
        Assert.False(await ctx.ArchivedEmails.AnyAsync(e => e.Id == expired.Id));
        Assert.True(await ctx.ArchivedEmails.AnyAsync(e => e.Id == kept.Id));

        var proof = await ctx.ArchivedEmailOriginals.AsNoTracking().SingleAsync(o => o.ArchivedEmailId == expired.Id);
        Assert.NotNull(proof.PurgedAt);
        Assert.Null(proof.RawMime);
        Assert.Equal("Retention period expired", proof.PurgeReason);

        Assert.True(await ctx.AccessLogs.AnyAsync(l => l.OrganizationId == org.Id && l.Type == AccessLogType.RetentionPurge));
        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        Assert.Equal(IntegrityCheckStatus.Ok, run.Status);
    }

    [Fact]
    public async Task OriginalBytes_CannotBePurgedBeforeRetentionExpires()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 10);
        var email = await ArchiveWithOriginalAsync(ctx, await CreateAccountAsync(ctx, org), "early purge");

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmailOriginals.Where(o => o.ArchivedEmailId == email.Id)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(o => o.RawMime, (byte[]?)null)
                    .SetProperty(o => o.PurgedAt, DateTime.UtcNow)));
        Assert.Equal("MA001", ex.SqlState);
    }

    [Fact]
    public async Task LegacyRows_GetTheirRetentionBackfilled()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx, retentionYears: 8);
        var email = NewEmail(await CreateAccountAsync(ctx, org), sent: new DateTime(2020, 5, 5));
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();

        // Simulate a row from before MigrateFork_3 (in a transaction, so the trigger cannot stay off).
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmails\" DISABLE TRIGGER ma_email_retention_update");
            await ctx.ArchivedEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.RetainUntil, (DateTime?)null));
            await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmails\" ENABLE TRIGGER ma_email_retention_update");
            await tx.CommitAsync();
        }

        // Still protected while RetainUntil is missing: computed on the fly.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmails.Where(e => e.Id == email.Id).ExecuteDeleteAsync());
        Assert.Equal("MA001", ex.SqlState);

        while (await Retention(ctx).BackfillRetentionAsync() > 0) { }
        var stored = await ctx.ArchivedEmails.AsNoTracking().SingleAsync(e => e.Id == email.Id);
        Assert.Equal(new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.RetainUntil!.Value.ToUniversalTime());
    }
}
