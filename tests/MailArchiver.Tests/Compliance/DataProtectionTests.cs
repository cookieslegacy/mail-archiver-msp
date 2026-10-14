using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Compliance;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Compliance;

[Collection(TestDbFixture.CollectionName)]
public class DataProtectionTests : ComplianceTestBase
{
    public DataProtectionTests(TestDbFixture fixture) : base(fixture) { }

    private static DataProtectionService Service(MailArchiverDbContext ctx, int auditRetentionYears = 10) =>
        new(ctx, Options.Create(new ComplianceOptions { AuditLogRetentionYears = auditRetentionYears }),
            NullLogger<DataProtectionService>.Instance);

    private static ArchivedEmail EmailWith(MailAccount account, string from, string to, string subject)
    {
        var email = NewEmail(account, subject);
        email.From = from;
        email.To = to;
        return email;
    }

    [Fact]
    public async Task AccessRequest_FindsThePersonAcrossAllMailAccounts_AndOnlyInsideTheOrganization()
    {
        int orgA, orgB;
        await using (var system = SystemContext())
        {
            var a = await CreateOrganizationAsync(system);
            var b = await CreateOrganizationAsync(system);
            orgA = a.Id;
            orgB = b.Id;
            var sales = await CreateAccountAsync(system, a);
            var support = await CreateAccountAsync(system, a);
            var other = await CreateAccountAsync(system, b);
            system.ArchivedEmails.AddRange(
                EmailWith(sales, "\"Max Mustermann\" <max.mustermann@example.org>", "sales@kunde.test", "offer"),
                EmailWith(support, "support@kunde.test", "Max <MAX.MUSTERMANN@example.org>", "ticket"),
                EmailWith(support, "someone@else.test", "support@kunde.test", "unrelated"),
                EmailWith(other, "max.mustermann@example.org", "other@kunde-b.test", "other org"));
            await system.SaveChangesAsync();
        }

        await using (var ctx = ContextFor(orgA))
        {
            var result = await Service(ctx).FindEmailsOfAsync("max.mustermann@example.org");
            Assert.Equal(2, result.Total);
            Assert.Equal(new[] { "offer", "ticket" }, result.Emails.Select(e => e.Subject).OrderBy(s => s));
            Assert.Equal(2, result.PerAccount.Count);
            Assert.False(result.Truncated);

            // The % and _ of LIKE are literal in the address.
            Assert.Equal(0, (await Service(ctx).FindEmailsOfAsync("%@example.org")).Total);

            var csv = System.Text.Encoding.UTF8.GetString(DataProtectionService.OverviewCsv(result));
            Assert.Contains("Id;MailAccount;SentDate;From;To;Cc;Subject", csv);
            Assert.Contains("offer", csv);
        }

        await using (var ctx = ContextFor(orgB))
        {
            Assert.Equal(1, (await Service(ctx).FindEmailsOfAsync("max.mustermann@example.org")).Total);
        }
    }

    [Fact]
    public async Task RestrictedEmail_IsHiddenFromUsers_ButNotFromAdminsAuditorsOrSystem()
    {
        int orgId, emailId;
        await using (var system = SystemContext())
        {
            var org = await CreateOrganizationAsync(system, retentionYears: 10);
            orgId = org.Id;
            var account = await CreateAccountAsync(system, org);
            var email = await ArchiveWithOriginalAsync(system, account, "restricted");
            emailId = email.Id;
            // Locked emails may still be restricted: the trigger excludes these columns.
            await system.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = true WHERE \"Id\" = {emailId}");
        }

        await using (var admin = ContextFor(orgId, canSeeRestricted: true))
        {
            var changed = await Service(admin).SetRestrictionAsync(new[] { emailId }, true, "objection", "admin");
            Assert.Equal(1, changed);
            Assert.True(await admin.ArchivedEmails.AnyAsync(e => e.Id == emailId));
            Assert.Single(await Service(admin).RestrictedEmailsAsync());
            Assert.True(await admin.AccessLogs.AnyAsync(l => l.Type == AccessLogType.ProcessingRestriction && l.EmailId == emailId));
        }

        await using (var user = ContextFor(orgId, canSeeRestricted: false))
        {
            Assert.False(await user.ArchivedEmails.AnyAsync(e => e.Id == emailId));
            Assert.False(await user.EmailAttachments.AnyAsync(a => a.ArchivedEmailId == emailId));
            Assert.Equal(0, (await Service(user).FindEmailsOfAsync("a@example.com")).Total);
        }

        await using (var system = SystemContext())
        {
            // Retention and the chain are untouched; the system still sees the email.
            Assert.True(await system.ArchivedEmails.AnyAsync(e => e.Id == emailId && e.ProcessingRestricted));
            Assert.True(await system.ArchivedEmailOriginals.AnyAsync(o => o.ArchivedEmailId == emailId));
        }

        await using (var admin = ContextFor(orgId, canSeeRestricted: true))
        {
            Assert.Equal(1, await Service(admin).SetRestrictionAsync(new[] { emailId }, false, null, "admin"));
        }
        await using (var user = ContextFor(orgId, canSeeRestricted: false))
        {
            Assert.True(await user.ArchivedEmails.AnyAsync(e => e.Id == emailId));
        }
    }

    [Fact]
    public async Task AuditLogPrune_RemovesOnlyTheOldPrefix_RecordsTheCut_AndTheChainStillVerifies()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var longAgo = new DateTime(2010, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            ctx.AccessLogs.Add(new AccessLog { Username = "old-user", Type = AccessLogType.Login, Timestamp = longAgo.AddDays(i), OrganizationId = org.Id, SearchParameters = "IP: 10.0.0.1" });
        for (var i = 0; i < 3; i++)
            ctx.AccessLogs.Add(new AccessLog { Username = "new-user", Type = AccessLogType.Search, Timestamp = DateTime.UtcNow.AddDays(-1 + i), OrganizationId = org.Id, SearchParameters = "recent" });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var fifth = await ctx.AccessLogs.AsNoTracking().SingleAsync(l => l.OrganizationId == org.Id && l.ChainSeq == 5);

        // Direct deletes are still refused.
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.AccessLogs.Where(l => l.OrganizationId == org.Id && l.ChainSeq == 1).ExecuteDeleteAsync());
        Assert.Contains("append-only", refused.MessageText);

        var removed = await Service(ctx, auditRetentionYears: 10).PruneAuditLogAsync(org.Id, "test");
        Assert.Equal(5, removed);

        var remaining = await ctx.AccessLogs.AsNoTracking().Where(l => l.OrganizationId == org.Id).OrderBy(l => l.ChainSeq).ToListAsync();
        Assert.DoesNotContain(remaining, l => l.Username == "old-user");
        Assert.Equal(6, remaining.First().ChainSeq);
        Assert.Equal(fifth.ChainHash, remaining.First().PrevChainHash);
        // The prune itself is logged, as entry #9.
        Assert.Contains(remaining, l => l.Type == AccessLogType.AuditLogPrune);

        var prune = await ctx.AuditLogPrunes.AsNoTracking().SingleAsync(p => p.OrganizationId == org.Id);
        Assert.Equal(5, prune.PrunedThroughSeq);
        Assert.Equal(fifth.ChainHash, prune.PrunedThroughHash);
        Assert.Equal(5, prune.EntriesRemoved);

        // A second run finds nothing older; the chain verifies from the recorded cut.
        Assert.Equal(0, await Service(ctx).PruneAuditLogAsync(org.Id, "test"));
        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        Assert.Equal("Ok", run.Status);
        Assert.Equal(0, run.AuditEntriesFailed);
        Assert.True(run.AuditEntriesChecked >= 4);

        // Retention 0 = keep forever.
        Assert.Equal(0, await Service(ctx, auditRetentionYears: 0).PruneAuditLogAsync(org.Id, "test"));
    }

    [Fact]
    public async Task AuditLogPrune_DoesNotTouchOtherOrganizations_OrEntriesWithinRetention()
    {
        await using var ctx = SystemContext();
        var a = await CreateOrganizationAsync(ctx);
        var b = await CreateOrganizationAsync(ctx);
        var longAgo = new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.AccessLogs.Add(new AccessLog { Username = "a-old", Type = AccessLogType.Login, Timestamp = longAgo, OrganizationId = a.Id });
        ctx.AccessLogs.Add(new AccessLog { Username = "a-new", Type = AccessLogType.Login, Timestamp = DateTime.UtcNow, OrganizationId = a.Id });
        ctx.AccessLogs.Add(new AccessLog { Username = "b-old", Type = AccessLogType.Login, Timestamp = longAgo, OrganizationId = b.Id });
        ctx.AccessLogs.Add(new AccessLog { Username = "b-new", Type = AccessLogType.Login, Timestamp = DateTime.UtcNow, OrganizationId = b.Id });
        await ctx.SaveChangesAsync();

        Assert.Equal(1, await Service(ctx).PruneAuditLogAsync(a.Id, "test"));
        Assert.True(await ctx.AccessLogs.AnyAsync(l => l.Username == "b-old"));
        Assert.True(await ctx.AccessLogs.AnyAsync(l => l.Username == "a-new"));
        Assert.False(await ctx.AccessLogs.AnyAsync(l => l.Username == "a-old"));

        // Nothing is removed when the retention has not passed for anything.
        Assert.Equal(0, await Service(ctx, auditRetentionYears: 50).PruneAuditLogAsync(b.Id, "test"));
    }
}
