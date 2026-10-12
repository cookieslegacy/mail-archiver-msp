using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailArchiver.Models;

namespace MailArchiver.Services.Compliance
{
    /// <summary>
    /// The canonical chain inputs, computed independently of the database. They must produce
    /// exactly the text of the SQL functions <c>ma_original_chain_input</c> (MigrateFork_2) and
    /// <c>ma_audit_chain_input</c> (MigrateFork_4). The verification recomputes every link here,
    /// so a database that rewrote its own chain functions would still be caught.
    /// </summary>
    public static class IntegrityChain
    {
        public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string Sha256Hex(byte[] data) =>
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

        /// <summary>Microseconds since the epoch of a timestamptz value.</summary>
        public static long EpochMicros(DateTime value)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                // Npgsql returns timestamptz as Local or Utc; Unspecified only comes from
                // "timestamp without time zone" columns, which hold UTC in this application.
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
            return (utc - UnixEpoch).Ticks / 10;
        }

        public static string OriginalChainInput(ArchivedEmailOriginal o) => string.Join('|',
            "MA-ORIGINAL-v1",
            o.OrganizationId.ToString(CultureInfo.InvariantCulture),
            o.ChainSeq.ToString(CultureInfo.InvariantCulture),
            o.PrevChainHash,
            o.ArchivedEmailId.ToString(CultureInfo.InvariantCulture),
            o.MailAccountId.ToString(CultureInfo.InvariantCulture),
            o.Source,
            o.ContentSha256,
            o.SizeBytes.ToString(CultureInfo.InvariantCulture),
            EpochMicros(o.ArchivedAt).ToString(CultureInfo.InvariantCulture),
            o.MessageId ?? string.Empty);

        public static string OriginalChainHash(ArchivedEmailOriginal o) => Sha256Hex(OriginalChainInput(o));

        public static string AuditChainInput(AccessLog l) => string.Join('|',
            "MA-AUDIT-v1",
            (l.OrganizationId ?? 0).ToString(CultureInfo.InvariantCulture),
            l.ChainSeq.ToString(CultureInfo.InvariantCulture),
            l.PrevChainHash ?? string.Empty,
            l.Id.ToString(CultureInfo.InvariantCulture),
            LengthPrefixed(l.Username),
            ((int)l.Type).ToString(CultureInfo.InvariantCulture),
            EpochMicros(l.Timestamp).ToString(CultureInfo.InvariantCulture),
            l.EmailId?.ToString(CultureInfo.InvariantCulture) ?? "-",
            LengthPrefixed(l.EmailSubject),
            LengthPrefixed(l.EmailFrom),
            LengthPrefixed(l.SearchParameters),
            l.MailAccountId?.ToString(CultureInfo.InvariantCulture) ?? "-");

        public static string AuditChainHash(AccessLog l) => Sha256Hex(AuditChainInput(l));

        private static string LengthPrefixed(string? value) =>
            value == null ? "-" : Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value;

        /// <summary>
        /// The seal of an email archived before originals were kept: a SHA-256 over the stored
        /// fields and attachment payload hashes, in a fixed, length-prefixed order. It proves
        /// that these fields have not changed since the seal was made.
        /// </summary>
        public static string LegacySeal(ArchivedEmail e, IEnumerable<(string FileName, string ContentType, string PayloadSha256)> attachments)
        {
            var sb = new StringBuilder("MA-LEGACY-SEAL-v1");
            void Add(string? v) => sb.Append('|').Append(LengthPrefixed(v));

            Add(e.Id.ToString(CultureInfo.InvariantCulture));
            Add(e.MailAccountId.ToString(CultureInfo.InvariantCulture));
            // MessageId and FolderName are left out: the sync may legitimately heal a fallback
            // Message-ID and move an email between folders after it was sealed.
            Add(e.Subject);
            Add(e.From);
            Add(e.To);
            Add(e.Cc);
            Add(e.Bcc);
            Add(EpochMicros(e.SentDate).ToString(CultureInfo.InvariantCulture));
            Add(EpochMicros(e.ReceivedDate).ToString(CultureInfo.InvariantCulture));
            Add(e.IsOutgoing ? "1" : "0");
            Add(e.RawHeaders);
            Add(e.BodyUntruncatedText ?? e.Body);
            Add(e.BodyUntruncatedHtml ?? e.HtmlBody);
            foreach (var a in attachments.OrderBy(a => a.PayloadSha256, StringComparer.Ordinal)
                         .ThenBy(a => a.FileName, StringComparer.Ordinal))
            {
                Add(a.FileName);
                Add(a.ContentType);
                Add(a.PayloadSha256);
            }
            return Sha256Hex(sb.ToString());
        }
    }
}
