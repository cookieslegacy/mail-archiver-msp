using MailArchiver.Models;
using MailArchiver.Services.Compliance;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.Compliance;

[Collection(TestDbFixture.CollectionName)]
public class ArchiveIntegrityTests : ComplianceTestBase
{
    public ArchiveIntegrityTests(TestDbFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Originals_AreChainedByTheDatabase_AndTheChainVerifiesInCSharp()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);

        var first = await ArchiveWithOriginalAsync(ctx, account, "first");
        var second = await ArchiveWithOriginalAsync(ctx, account, "second");

        var originals = await ctx.ArchivedEmailOriginals.AsNoTracking()
            .Where(o => o.OrganizationId == org.Id).OrderBy(o => o.ChainSeq).ToListAsync();
        Assert.Equal(2, originals.Count);
        Assert.Equal(new long[] { 1, 2 }, originals.Select(o => o.ChainSeq));
        Assert.Equal(IntegrityChain.GenesisHash, originals[0].PrevChainHash);
        Assert.Equal(originals[0].ChainHash, originals[1].PrevChainHash);
        Assert.Equal(first.Id, originals[0].ArchivedEmailId);
        Assert.Equal(second.Id, originals[1].ArchivedEmailId);

        foreach (var original in originals)
        {
            // The database computed these; C# must arrive at the same values independently.
            Assert.Equal(IntegrityChain.OriginalChainHash(original), original.ChainHash);
            Assert.Equal(IntegrityChain.Sha256Hex(original.RawMime!), original.ContentSha256);
            Assert.Equal(original.RawMime!.LongLength, original.SizeBytes);
        }

        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        Assert.Equal(IntegrityCheckStatus.Ok, run.Status);
        Assert.Equal(2, run.OriginalsChecked);
        Assert.Equal(originals[1].ChainHash, run.OriginalsHeadHash);
    }

    [Fact]
    public async Task OriginalWithWrongHash_IsRejectedOnInsert()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);

        var email = NewEmail(account);
        email.Original = new ArchivedEmailOriginal
        {
            Source = OriginalSources.Imap,
            RawMime = RawMessage("x"),
            ContentSha256 = new string('a', 64),
            SizeBytes = 1
        };
        ctx.ArchivedEmails.Add(email);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Contains("does not match its bytes", FindPostgresException(ex)!.MessageText);
    }

    [Fact]
    public async Task Originals_CannotBeChangedOrDeleted()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var email = await ArchiveWithOriginalAsync(ctx, await CreateAccountAsync(ctx, org), "immutable");
        var id = email.Original!.Id;

        var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmailOriginals.Where(o => o.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.ContentSha256, new string('b', 64))));
        Assert.Contains("immutable", update.MessageText);

        var overwrite = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmailOriginals.Where(o => o.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.RawMime, new byte[] { 1 })));
        Assert.Contains("cannot be written", overwrite.MessageText);

        var delete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.ArchivedEmailOriginals.Where(o => o.Id == id).ExecuteDeleteAsync());
        Assert.Contains("append-only", delete.MessageText);
    }

    [Fact]
    public async Task Verification_DetectsTamperingBehindTheTriggers()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);
        await ArchiveWithOriginalAsync(ctx, account, "one");
        var victim = await ArchiveWithOriginalAsync(ctx, account, "two");
        await ArchiveWithOriginalAsync(ctx, account, "three");

        // Someone with full database rights switches the protection off and edits an original.
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmailOriginals\" DISABLE TRIGGER USER");
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE mail_archiver.\"ArchivedEmailOriginals\" SET \"RawMime\" = convert_to('forged', 'UTF8') WHERE \"ArchivedEmailId\" = {victim.Id}");
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmailOriginals\" ENABLE TRIGGER USER");

        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        await tx.RollbackAsync();

        Assert.Equal(IntegrityCheckStatus.Failed, run.Status);
        Assert.Equal(1, run.OriginalsFailed);
        Assert.Contains("does not match its hash", run.Problems);
    }

    [Fact]
    public async Task Verification_DetectsARewrittenChainThroughItsAnchor()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);
        await ArchiveWithOriginalAsync(ctx, account, "one");
        await ArchiveWithOriginalAsync(ctx, account, "two");

        var anchors = await new AnchorService(ctx, DisabledWormStore.Instance,
                Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()), new NoHttpClientFactory(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnchorService>.Instance)
            .AnchorAsync(org.Id);
        Assert.Contains(anchors, a => a.ChainKind == IntegrityChainKinds.Originals && a.ChainSeq == 2);

        // A consistent rewrite of the whole chain (all hashes recomputed) passes the link check,
        // but no longer matches the anchor taken before.
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmailOriginals\" DISABLE TRIGGER USER");
        await ctx.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE mail_archiver.""ArchivedEmailOriginals"" o
            SET ""MessageId"" = 'rewritten-' || o.""ChainSeq""
            WHERE o.""OrganizationId"" = {org.Id}");
        // Recompute both links in order, as an attacker would.
        for (var seq = 1; seq <= 2; seq++)
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE mail_archiver.""ArchivedEmailOriginals"" o
                SET ""PrevChainHash"" = COALESCE((SELECT p.""ChainHash"" FROM mail_archiver.""ArchivedEmailOriginals"" p
                                                  WHERE p.""OrganizationId"" = o.""OrganizationId"" AND p.""ChainSeq"" = o.""ChainSeq"" - 1),
                                                 repeat('0', 64))
                WHERE o.""OrganizationId"" = {org.Id} AND o.""ChainSeq"" = {seq}");
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE mail_archiver.""ArchivedEmailOriginals"" o
                SET ""ChainHash"" = encode(digest(mail_archiver.ma_original_chain_input(o), 'sha256'), 'hex')
                WHERE o.""OrganizationId"" = {org.Id} AND o.""ChainSeq"" = {seq}");
        }
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE mail_archiver.\"ArchivedEmailOriginals\" ENABLE TRIGGER USER");

        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        await tx.RollbackAsync();

        Assert.Equal(0, run.OriginalsFailed);
        Assert.Equal(1, run.AnchorsFailed);
        Assert.Equal(IntegrityCheckStatus.Failed, run.Status);
    }

    [Fact]
    public async Task AuditLog_IsChained_AppendOnly_AndVerifiable()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);

        ctx.AccessLogs.Add(new AccessLog { Username = "alice", Type = AccessLogType.Open, MailAccountId = account.Id, EmailSubject = "a|b", Timestamp = DateTime.UtcNow });
        ctx.AccessLogs.Add(new AccessLog { Username = "bob", Type = AccessLogType.Search, OrganizationId = org.Id, SearchParameters = "Grüße ✓", Timestamp = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var entries = await ctx.AccessLogs.AsNoTracking().Where(l => l.OrganizationId == org.Id).OrderBy(l => l.ChainSeq).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.ChainSeq));
        Assert.All(entries, e => Assert.Equal(IntegrityChain.AuditChainHash(e), e.ChainHash));

        var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.AccessLogs.Where(l => l.Id == entries[0].Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.Username, "mallory")));
        Assert.Contains("append-only", update.MessageText);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.AccessLogs.Where(l => l.Id == entries[0].Id).ExecuteDeleteAsync());

        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        Assert.Equal(0, run.AuditEntriesFailed);
        // The verification wrote its own entry, chained as #3.
        Assert.True(await ctx.AccessLogs.AnyAsync(l => l.OrganizationId == org.Id && l.ChainSeq == 3 && l.Type == AccessLogType.IntegrityCheck));
    }

    [Fact]
    public async Task LegacyEmails_AreSealed_AndChangesAfterwardsAreDetected()
    {
        await using var ctx = SystemContext();
        var org = await CreateOrganizationAsync(ctx);
        var account = await CreateAccountAsync(ctx, org);

        // Written the way the upstream code paths without an original write it.
        var email = NewEmail(account, "legacy");
        email.Attachments.Add(new EmailAttachment { FileName = "l.txt", ContentType = "text/plain", Content = new byte[] { 7, 7 }, Size = 2 });
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var retention = Retention(ctx);
        int? cursor = email.Id - 1;
        while (cursor != null)
            cursor = (await retention.SealLegacyEmailsAsync(cursor.Value)).LastId;

        var seal = await ctx.ArchivedEmailOriginals.AsNoTracking().SingleAsync(o => o.ArchivedEmailId == email.Id);
        Assert.Equal(OriginalSources.LegacySeal, seal.Source);
        Assert.Null(seal.RawMime);
        Assert.Equal(IntegrityCheckStatus.Ok, (await Verifier(ctx).VerifyAsync(org.Id, "test")).Status);

        await ctx.ArchivedEmails.Where(e => e.Id == email.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Subject, "changed later"));
        var run = await Verifier(ctx).VerifyAsync(org.Id, "test");
        Assert.Equal(IntegrityCheckStatus.Failed, run.Status);
        Assert.Contains("changed after sealing", run.Problems);
    }

    [Fact]
    public void MboxSlice_IsTheMessageWithoutFraming()
    {
        var mbox = "From a@b Mon Jan 1 00:00:00 2024\nSubject: one\n\nbody one\n>From quoted\n\nFrom c@d Mon Jan 1 00:00:00 2024\nSubject: two\n\nbody two\n";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, mbox);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
            var first = MailArchiver.Services.Providers.MBox.MBoxStreamProcessor.ReadRawMessage(file, 0, 70);
            var second = MailArchiver.Services.Providers.MBox.MBoxStreamProcessor.ReadRawMessage(file, 70, mbox.Length);
            Assert.Equal("Subject: one\n\nbody one\n>From quoted\n", System.Text.Encoding.ASCII.GetString(first!));
            Assert.Equal("Subject: two\n\nbody two\n", System.Text.Encoding.ASCII.GetString(second!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in tests");
    }
}
