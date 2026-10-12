using MailArchiver.Models;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.Compliance;

[Collection(TestDbFixture.CollectionName)]
public class OrganizationIsolationTests : ComplianceTestBase
{
    public OrganizationIsolationTests(TestDbFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Email_TakesItsOrganizationFromTheAccount_NotFromTheCaller()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);

        var email = NewEmail(account);
        email.OrganizationId = Organization.DefaultOrganizationId; // a lie, ignored by the trigger
        email.Attachments.Add(new EmailAttachment { FileName = "a.txt", ContentType = "text/plain", Content = new byte[] { 1, 2, 3 }, Size = 3 });
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();

        var stored = await ctx.ArchivedEmails.AsNoTracking().Include(e => e.Attachments).SingleAsync(e => e.Id == email.Id);
        Assert.Equal(org.Id, stored.OrganizationId);
        Assert.All(stored.Attachments, a => Assert.Equal(org.Id, a.OrganizationId));
    }

    [Fact]
    public async Task QueryFilter_HidesOtherOrganizations()
    {
        int orgA, orgB, emailB, accountB;
        await using (var ctx = SystemContext())
        {
            var a = await CreateOrganizationAsync(ctx);
            var b = await CreateOrganizationAsync(ctx);
            var accA = await CreateAccountAsync(ctx, a);
            var accB = await CreateAccountAsync(ctx, b);
            ctx.ArchivedEmails.Add(NewEmail(accA, "mail of A"));
            var mailB = NewEmail(accB, "mail of B");
            ctx.ArchivedEmails.Add(mailB);
            await ctx.SaveChangesAsync();
            (orgA, orgB, emailB, accountB) = (a.Id, b.Id, mailB.Id, accB.Id);
        }

        await using (var userOfA = ContextFor(orgA))
        {
            Assert.Null(await userOfA.ArchivedEmails.FirstOrDefaultAsync(e => e.Id == emailB));
            Assert.Null(await userOfA.MailAccounts.FirstOrDefaultAsync(a => a.Id == accountB));
            Assert.All(await userOfA.ArchivedEmails.Select(e => e.OrganizationId).Distinct().ToListAsync(),
                id => Assert.Equal(orgA, id));
        }

        // A provider admin without support access sees no organization data at all.
        await using (var provider = ContextFor(null, providerAdmin: true))
        {
            Assert.False(await provider.ArchivedEmails.AnyAsync());
            Assert.False(await provider.MailAccounts.AnyAsync());
        }

        await using (var system = SystemContext())
        {
            Assert.NotNull(await system.ArchivedEmails.FirstOrDefaultAsync(e => e.Id == emailB));
            Assert.True(await system.ArchivedEmails.AnyAsync(e => e.OrganizationId == orgB));
        }
    }

    [Fact]
    public async Task RowLevelSecurity_HidesOtherOrganizations_EvenForRawSql()
    {
        int orgA, orgB;
        await using (var ctx = SystemContext())
        {
            var a = await CreateOrganizationAsync(ctx);
            var b = await CreateOrganizationAsync(ctx);
            ctx.ArchivedEmails.Add(NewEmail(await CreateAccountAsync(ctx, a)));
            ctx.ArchivedEmails.Add(NewEmail(await CreateAccountAsync(ctx, b)));
            await ctx.SaveChangesAsync();
            (orgA, orgB) = (a.Id, b.Id);
        }

        // Row level security does not bind superusers, so the check runs as a plain role.
        await using var conn = new Npgsql.NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task<long> Scalar(string sql)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        async Task Exec(string sql)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }

        var role = "ma_rls_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        await Exec($"GRANT USAGE ON SCHEMA mail_archiver TO {role}");
        await Exec($"GRANT SELECT ON ALL TABLES IN SCHEMA mail_archiver TO {role}");
        await Exec($"SET LOCAL ROLE {role}");

        await Exec($"SELECT set_config('mail_archiver.org_id', '{orgA}', true)");
        Assert.Equal(0, await Scalar($"SELECT count(*) FROM mail_archiver.\"ArchivedEmails\" WHERE \"OrganizationId\" = {orgB}"));
        Assert.Equal(1, await Scalar($"SELECT count(*) FROM mail_archiver.\"ArchivedEmails\" WHERE \"OrganizationId\" = {orgA}"));

        await Exec("SELECT set_config('mail_archiver.org_id', '-1', true)");
        Assert.Equal(0, await Scalar("SELECT count(*) FROM mail_archiver.\"MailAccounts\""));

        await Exec("SELECT set_config('mail_archiver.org_id', '', true)");
        Assert.Equal(1, await Scalar($"SELECT count(*) FROM mail_archiver.\"ArchivedEmails\" WHERE \"OrganizationId\" = {orgB}"));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task UserCannotBeGivenAnAccountOfAnotherOrganization()
    {
        await using var ctx = SystemContext();
        var a = await CreateOrganizationAsync(ctx);
        var b = await CreateOrganizationAsync(ctx);
        var accountB = await CreateAccountAsync(ctx, b);
        var user = new User
        {
            Username = "u-" + Guid.NewGuid().ToString("N")[..10],
            Email = Guid.NewGuid().ToString("N")[..10] + "@test.local",
            OrganizationId = a.Id
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        ctx.UserMailAccounts.Add(new UserMailAccount { UserId = user.Id, MailAccountId = accountB.Id });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Contains("different organizations", FindPostgresException(ex)!.MessageText);
    }

    [Fact]
    public async Task AttachmentPayloads_AreDeduplicatedPerOrganization()
    {
        await using var ctx = SystemContext();
        var a = await CreateOrganizationAsync(ctx);
        var b = await CreateOrganizationAsync(ctx);
        var payload = System.Text.Encoding.UTF8.GetBytes("shared payload " + Guid.NewGuid());

        foreach (var account in new[] { await CreateAccountAsync(ctx, a), await CreateAccountAsync(ctx, a), await CreateAccountAsync(ctx, b) })
        {
            var email = NewEmail(account);
            email.Attachments.Add(new EmailAttachment { FileName = "x.bin", ContentType = "application/octet-stream", Content = payload, Size = payload.Length });
            ctx.ArchivedEmails.Add(email);
            await ctx.SaveChangesAsync();
        }

        var hash = MailArchiver.Services.Compliance.IntegrityChain.Sha256Hex(payload);
        var rows = await ctx.AttachmentContents.AsNoTracking().Where(c => c.Hash == hash).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.OrganizationId == a.Id);
        Assert.Contains(rows, r => r.OrganizationId == b.Id);

        var linked = await ctx.EmailAttachments.AsNoTracking()
            .Where(x => x.AttachmentContent!.Hash == hash)
            .Select(x => new { x.OrganizationId, ContentOrg = x.AttachmentContent!.OrganizationId })
            .ToListAsync();
        Assert.Equal(3, linked.Count);
        Assert.All(linked, l => Assert.Equal(l.OrganizationId, l.ContentOrg));
    }

    [Fact]
    public async Task NewAccounts_InheritTheOrganizationOfTheirCreator()
    {
        int orgId;
        await using (var ctx = SystemContext())
            orgId = (await CreateOrganizationAsync(ctx)).Id;

        await using var userCtx = ContextFor(orgId);
        var account = new MailAccount
        {
            Name = "created-by-user", EmailAddress = Guid.NewGuid().ToString("N") + "@test.local",
            Provider = ProviderType.IMAP, LastSync = DateTime.UtcNow
        };
        userCtx.MailAccounts.Add(account);
        await userCtx.SaveChangesAsync();
        Assert.Equal(orgId, account.OrganizationId);

        await using var noOrg = ContextFor(null, providerAdmin: true);
        noOrg.MailAccounts.Add(new MailAccount { Name = "x", EmailAddress = "x@test.local", LastSync = DateTime.UtcNow });
        await Assert.ThrowsAsync<InvalidOperationException>(() => noOrg.SaveChangesAsync());
    }
}
