using MailArchiver.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MailArchiver.Services.Compliance
{
    /// <summary>
    /// Lets the delete paths of the user interface tell protected emails apart before they ask
    /// the database (which refuses them anyway), and turns the database's refusal into a message.
    /// </summary>
    public static class RetentionGuard
    {
        /// <summary>Splits email ids into those that may be deleted now and those that are protected.</summary>
        public static async Task<(List<int> Deletable, List<int> Protected)> SplitAsync(
            MailArchiverDbContext context, IReadOnlyCollection<int> emailIds, CancellationToken ct = default)
        {
            var ids = emailIds.Distinct().ToList();
            var now = DateTime.UtcNow;
            var protectedIds = new List<int>();
            foreach (var chunk in ids.Chunk(5000))
            {
                protectedIds.AddRange(await context.ArchivedEmails
                    .Where(e => chunk.Contains(e.Id))
                    .Where(e => e.RetainUntil == null || e.RetainUntil > now || e.LegalHold || e.MailAccount.LegalHold
                                || context.Organizations.Any(o => o.Id == e.OrganizationId && o.LegalHold))
                    .Select(e => e.Id)
                    .ToListAsync(ct));
            }
            // Ids the context cannot see (other organization, deleted) are neither.
            var visible = new HashSet<int>();
            foreach (var chunk in ids.Chunk(5000))
                visible.UnionWith(await context.ArchivedEmails.Where(e => chunk.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct));
            var protectedSet = protectedIds.ToHashSet();
            return (ids.Where(id => visible.Contains(id) && !protectedSet.Contains(id)).ToList(), protectedIds);
        }

        /// <summary>Number of emails of a mail account that are still protected.</summary>
        public static async Task<int> CountProtectedInAccountAsync(MailArchiverDbContext context, int mailAccountId,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return await context.ArchivedEmails
                .Where(e => e.MailAccountId == mailAccountId)
                .CountAsync(e => e.RetainUntil == null || e.RetainUntil > now || e.LegalHold || e.MailAccount.LegalHold
                                 || context.Organizations.Any(o => o.Id == e.OrganizationId && o.LegalHold), ct);
        }

        /// <summary>A user facing message for a refusal by the retention triggers, or null.</summary>
        public static string? Describe(Exception exception, IStringLocalizer<ComplianceResource>? localizer)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is Npgsql.PostgresException pg)
                {
                    return pg.SqlState switch
                    {
                        "MA001" => localizer?["DeleteRefusedRetention"].Value ?? "The email is still within its legal retention period.",
                        "MA002" => localizer?["DeleteRefusedLegalHold"].Value ?? "The email is on legal hold.",
                        "MA003" => localizer?["RetentionCannotBeShortened"].Value ?? "A retention period cannot be shortened.",
                        _ => null
                    };
                }
            }
            return null;
        }
    }
}
