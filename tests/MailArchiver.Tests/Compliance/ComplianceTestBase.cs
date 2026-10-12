using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Compliance;
using MailArchiver.Services.Organizations;
using MailArchiver.Services.Security;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Compliance;

/// <summary>
/// Helpers for the organization and compliance tests. Every test works in organizations of its
/// own, so hash chains start at #1 and nothing interferes with other tests. Organizations,
/// originals and audit entries are append-only or referenced and therefore stay in the test
/// database; that is intended.
/// </summary>
public abstract class ComplianceTestBase
{
    protected readonly TestDbFixture Fixture;
    private readonly DbContextOptions<MailArchiverDbContext> _options;

    protected ComplianceTestBase(TestDbFixture fixture)
    {
        Fixture = fixture;
        _options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(
                new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance),
                new OrganizationConnectionInterceptor())
            .Options;
    }

    /// <summary>A context in system scope (sees every organization).</summary>
    protected MailArchiverDbContext SystemContext() =>
        new(_options, NullCredentialProtector.Instance, SystemOrganizationContext.Instance);

    /// <summary>A context of a signed in user of the given organization.</summary>
    protected MailArchiverDbContext ContextFor(int? organizationId, bool providerAdmin = false) =>
        new(_options, NullCredentialProtector.Instance, new FixedOrganizationContext(organizationId, providerAdmin));

    protected static async Task<Organization> CreateOrganizationAsync(MailArchiverDbContext ctx, int retentionYears = 0)
    {
        var slug = "t-" + Guid.NewGuid().ToString("N")[..12];
        var organization = new Organization { Name = slug, Slug = slug, RetentionYears = retentionYears };
        ctx.Organizations.Add(organization);
        await ctx.SaveChangesAsync();
        return organization;
    }

    protected static async Task<MailAccount> CreateAccountAsync(MailArchiverDbContext ctx, Organization organization,
        int? retentionYears = null)
    {
        var account = new MailAccount
        {
            Name = "acct-" + Guid.NewGuid().ToString("N")[..10],
            EmailAddress = Guid.NewGuid().ToString("N") + "@test.local",
            Provider = ProviderType.IMAP,
            IsEnabled = true,
            LastSync = DateTime.UtcNow,
            OrganizationId = organization.Id,
            RetentionYears = retentionYears
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        return account;
    }

    protected static ArchivedEmail NewEmail(MailAccount account, string subject = "subject", DateTime? sent = null) => new()
    {
        MailAccountId = account.Id,
        MessageId = Guid.NewGuid().ToString("N") + "@test.local",
        Subject = subject,
        From = "a@example.com",
        To = "b@example.com",
        Cc = string.Empty,
        Bcc = string.Empty,
        Body = "body of " + subject,
        HtmlBody = string.Empty,
        SentDate = sent ?? new DateTime(2026, 3, 1, 10, 0, 0),
        ReceivedDate = sent ?? new DateTime(2026, 3, 1, 10, 0, 0),
        FolderName = "INBOX"
    };

    protected static byte[] RawMessage(string subject, string body = "Hello") => System.Text.Encoding.UTF8.GetBytes(
        "From: a@example.com\r\n" +
        "To: b@example.com\r\n" +
        $"Subject: {subject}\r\n" +
        $"Message-ID: <{Guid.NewGuid():N}@test.local>\r\n" +
        "Date: Sun, 01 Mar 2026 10:00:00 +0000\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        body + "\r\n");

    /// <summary>Archives an email with its original through the same service the sync uses.</summary>
    protected static async Task<ArchivedEmail> ArchiveWithOriginalAsync(MailArchiverDbContext ctx, MailAccount account,
        string subject, DateTime? sent = null)
    {
        var raw = RawMessage(subject);
        var email = NewEmail(account, subject, sent);
        await OriginalArchiveService.DatabaseOnly.AttachAsync(ctx, email, account, raw, null, OriginalSources.Imap);
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();
        return email;
    }

    protected static IntegrityVerificationService Verifier(MailArchiverDbContext ctx, IWormStore? worm = null) =>
        new(ctx, OriginalArchiveService.DatabaseOnly, worm ?? DisabledWormStore.Instance,
            NullLogger<IntegrityVerificationService>.Instance);

    protected static RetentionService Retention(MailArchiverDbContext ctx) =>
        new(ctx, OriginalArchiveService.DatabaseOnly, Options.Create(new ComplianceOptions()),
            NullLogger<RetentionService>.Instance);

    protected static Npgsql.PostgresException? FindPostgresException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is Npgsql.PostgresException pg)
                return pg;
        return null;
    }
}
