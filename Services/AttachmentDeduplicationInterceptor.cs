using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;


namespace MailArchiver.Services
{
    /// <summary>
    /// EF Core SaveChanges interceptor that deduplicates attachment payloads.
    ///
    /// For every newly added <see cref="EmailAttachment"/> the pending bytes are
    /// hashed (SHA-256) and stored content-addressed in the AttachmentContents
    /// table. Identical bytes are stored exactly once; the attachment only keeps a
    /// foreign key (<see cref="EmailAttachment.AttachmentContentId"/>) to the shared
    /// content row. This covers ALL write paths (IMAP, Graph, EML/MBOX import,
    /// truncated content saver, ...) without changing each individual call site.
    ///
    /// All attachments of a single SaveChanges are upserted in ONE database round-trip
    /// (via unnest arrays) instead of one statement per attachment. The upsert uses
    /// <c>INSERT ... ON CONFLICT ("Hash") DO NOTHING</c>; the unique index on Hash
    /// guarantees a single physical copy even under concurrent writers.
    ///
    /// There is intentionally NO reference counter: the authoritative cleanup of
    /// unreferenced content rows is the orphan garbage collection
    /// (<see cref="AttachmentDeduplicationBackgroundService"/> / DatabaseMaintenanceService),
    /// which is based on actual references and therefore robust against every deletion path.
    /// </summary>
    public class AttachmentDeduplicationInterceptor : SaveChangesInterceptor
    {
        private readonly ILogger<AttachmentDeduplicationInterceptor> _logger;

        public AttachmentDeduplicationInterceptor(ILogger<AttachmentDeduplicationInterceptor> logger)
        {
            _logger = logger;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.Context != null)
            {
                Deduplicate(eventData.Context);
            }
            return base.SavingChanges(eventData, result);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context != null)
            {
                await DeduplicateAsync(eventData.Context, cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        // ------------------------------------------------------------------
        // Shared preparation: collect pending attachments + their hashes.
        // ------------------------------------------------------------------

        /// <summary>
        /// An attachment plus the SHA-256 (hex) of its pending bytes, and what identifies its
        /// organization: the mail account of a new email, or the id of an existing email.
        /// </summary>
        private readonly record struct PendingAttachment(EmailAttachment Attachment, string Hash, byte[] Bytes,
            int? MailAccountId, int? ArchivedEmailId);

        private static List<PendingAttachment> CollectPending(DbContext context)
        {
            var entries = context.ChangeTracker
                .Entries<EmailAttachment>()
                .Where(e => e.State == EntityState.Added)
                .ToList();

            var pending = new List<PendingAttachment>(entries.Count);
            foreach (var entry in entries)
            {
                var attachment = entry.Entity;

                // Already deduplicated (e.g. set explicitly) – skip.
                if (attachment.AttachmentContentId.HasValue)
                    continue;

                var bytes = attachment.PendingContent ?? attachment.LegacyContent;
                if (bytes == null)
                    continue;

                // Payloads are deduplicated per organization. A new email has no id yet, so its
                // organization is found through its mail account.
                var email = attachment.ArchivedEmail;
                int? accountId = email != null && email.MailAccountId != 0 ? email.MailAccountId : null;
                int? emailId = attachment.ArchivedEmailId != 0 ? attachment.ArchivedEmailId
                    : email != null && email.Id != 0 ? email.Id : null;

                pending.Add(new PendingAttachment(attachment, ComputeSha256Hex(bytes), bytes, accountId, emailId));
            }
            return pending;
        }

        /// <summary>One input row per pending attachment for the batch upsert.</summary>
        private static (string[] hashes, byte[][] contents, long[] sizes, int?[] accounts, int?[] emails) BuildBatchArrays(
            List<PendingAttachment> pending)
        {
            var hashes = new string[pending.Count];
            var contents = new byte[pending.Count][];
            var sizes = new long[pending.Count];
            var accounts = new int?[pending.Count];
            var emails = new int?[pending.Count];
            for (var i = 0; i < pending.Count; i++)
            {
                hashes[i] = pending[i].Hash;
                contents[i] = pending[i].Bytes;
                sizes[i] = pending[i].Bytes.LongLength;
                accounts[i] = pending[i].MailAccountId;
                emails[i] = pending[i].ArchivedEmailId;
            }
            return (hashes, contents, sizes, accounts, emails);
        }

        /// <summary>Assigns the content row ids, indexed by the 1-based input position.</summary>
        private static void Assign(List<PendingAttachment> pending, IReadOnlyDictionary<long, int> ordinalToId)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (ordinalToId.TryGetValue(i + 1, out var id))
                {
                    var p = pending[i];
                    p.Attachment.AttachmentContentId = id;
                    p.Attachment.AttachmentContent = null; // avoid EF inserting a duplicate content row
                    p.Attachment.LegacyContent = null;     // never store bytes inline for new rows
                    p.Attachment.ClearPendingContent();
                }
            }
        }

        // SQL upsert for the whole batch in a single round-trip. Each input row resolves its
        // organization (from the mail account of a new email, or from an existing email);
        // payloads are unique per (organization, hash). The `ins` CTE returns the rows newly
        // inserted by this statement, the second join finds rows that already existed
        // (pre-snapshot) for inputs that hit ON CONFLICT DO NOTHING.
        private const string BatchUpsertSql = @"
            WITH input AS (
                SELECT u.h, u.c, u.s, u.ord,
                       COALESCE(
                           (SELECT a.""OrganizationId"" FROM mail_archiver.""MailAccounts"" a WHERE a.""Id"" = u.acct),
                           (SELECT e.""OrganizationId"" FROM mail_archiver.""ArchivedEmails"" e WHERE e.""Id"" = u.email)
                       ) AS org
                FROM unnest(@hashes, @contents, @sizes, @accounts, @emails)
                     WITH ORDINALITY AS u(h, c, s, acct, email, ord)
            ),
            ins AS (
                INSERT INTO mail_archiver.""AttachmentContents"" (""OrganizationId"", ""Hash"", ""Content"", ""Size"", ""CreatedAt"")
                SELECT DISTINCT ON (org, h) org, h, c, s, now() FROM input WHERE org IS NOT NULL
                ON CONFLICT (""OrganizationId"", ""Hash"") DO NOTHING
                RETURNING ""Id"", ""Hash"", ""OrganizationId""
            )
            SELECT i.ord, COALESCE(ins.""Id"", ac.""Id"")
            FROM input i
            LEFT JOIN ins ON ins.""OrganizationId"" = i.org AND ins.""Hash"" = i.h
            LEFT JOIN mail_archiver.""AttachmentContents"" ac ON ac.""OrganizationId"" = i.org AND ac.""Hash"" = i.h
            WHERE COALESCE(ins.""Id"", ac.""Id"") IS NOT NULL;";

        // ------------------------------------------------------------------
        // Async path
        // ------------------------------------------------------------------
        private async Task DeduplicateAsync(DbContext context, CancellationToken cancellationToken)
        {
            var pending = CollectPending(context);
            if (pending.Count == 0)
                return;

            var (hashes, contents, sizes, accounts, emails) = BuildBatchArrays(pending);

            var connection = context.Database.GetDbConnection();
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            // EF usually has not opened the connection yet at interception time, so we
            // open it ourselves and only close what we opened (openedHere). NOTE: when
            // there is no ambient transaction the content rows are committed independently
            // of the subsequent EF save; should that save fail, the (now unreferenced)
            // content rows are reclaimed by the authoritative orphan garbage collection.
            var openedHere = false;
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                openedHere = true;
            }

            try
            {
                var ordinalToId = new Dictionary<long, int>(hashes.Length);
                using var command = connection.CreateCommand();
                if (transaction != null)
                    command.Transaction = transaction;
                command.CommandText = BatchUpsertSql;
                AddArrayParameters(command, hashes, contents, sizes, accounts, emails);

                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                        ordinalToId[reader.GetInt64(0)] = reader.GetInt32(1);
                }

                Assign(pending, ordinalToId);
            }
            finally
            {
                if (openedHere && connection.State == ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }

        // ------------------------------------------------------------------
        // Sync path (true synchronous ADO.NET, no sync-over-async)
        // ------------------------------------------------------------------
        private void Deduplicate(DbContext context)
        {
            var pending = CollectPending(context);
            if (pending.Count == 0)
                return;

            var (hashes, contents, sizes, accounts, emails) = BuildBatchArrays(pending);

            var connection = context.Database.GetDbConnection();
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            var openedHere = false;
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
                openedHere = true;
            }

            try
            {
                var ordinalToId = new Dictionary<long, int>(hashes.Length);
                using var command = connection.CreateCommand();
                if (transaction != null)
                    command.Transaction = transaction;
                command.CommandText = BatchUpsertSql;
                AddArrayParameters(command, hashes, contents, sizes, accounts, emails);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        ordinalToId[reader.GetInt64(0)] = reader.GetInt32(1);
                }

                Assign(pending, ordinalToId);
            }
            finally
            {
                if (openedHere && connection.State == ConnectionState.Open)
                    connection.Close();
            }
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void AddArrayParameters(DbCommand command, string[] hashes, byte[][] contents, long[] sizes,
            int?[] accounts, int?[] emails)
        {
            AddParameter(command, "@hashes", hashes);
            AddParameter(command, "@contents", contents);
            AddParameter(command, "@sizes", sizes);
            AddParameter(command, "@accounts", accounts);
            AddParameter(command, "@emails", emails);
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
    }
}
