namespace MailArchiver.Models
{
    /// <summary>
    /// The original message of an archived email, exactly as it was received (RFC 5322 bytes),
    /// together with its SHA-256 and its link in the organization's hash chain.
    ///
    /// Rows are append-only: the database rejects deletes and every update except
    /// moving the bytes to WORM storage and purging the bytes after the retention period.
    /// The row itself stays as proof of what was archived and when it was deleted.
    ///
    /// The chain fields (<see cref="ChainSeq"/>, <see cref="PrevChainHash"/>,
    /// <see cref="ChainHash"/>, <see cref="ArchivedAt"/>) are computed by the database on
    /// insert, under a per-organization lock, so concurrent writers cannot fork the chain.
    /// </summary>
    public class ArchivedEmailOriginal
    {
        public long Id { get; set; }

        public int OrganizationId { get; set; }

        public int ArchivedEmailId { get; set; }
        public virtual ArchivedEmail? ArchivedEmail { get; set; }

        public int MailAccountId { get; set; }

        public string? MessageId { get; set; }

        /// <summary>Where the bytes came from, see <see cref="OriginalSources"/>.</summary>
        public string Source { get; set; } = OriginalSources.Imap;

        /// <summary>Lower case hex SHA-256 of the original bytes (or of the legacy seal).</summary>
        public string ContentSha256 { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        /// <summary>The original bytes when kept in the database; null in WORM mode or after purge.</summary>
        public byte[]? RawMime { get; set; }

        /// <summary>Object key in the WORM store, when the bytes live there.</summary>
        public string? StorageKey { get; set; }

        /// <summary>Object version in the WORM store (object lock requires versioned buckets).</summary>
        public string? StorageVersionId { get; set; }

        public DateTime ArchivedAt { get; set; }

        public DateTime? RetainUntil { get; set; }

        public long ChainSeq { get; set; }
        public string PrevChainHash { get; set; } = string.Empty;
        public string ChainHash { get; set; } = string.Empty;

        public DateTime? PurgedAt { get; set; }
        public string? PurgeReason { get; set; }
    }

    public static class OriginalSources
    {
        /// <summary>Byte exact copy fetched from the IMAP server.</summary>
        public const string Imap = "imap";
        /// <summary>Byte exact MIME content fetched from Microsoft Graph ($value).</summary>
        public const string Graph = "graph";
        /// <summary>Byte exact copy of an imported EML file.</summary>
        public const string ImportEml = "import-eml";
        /// <summary>Byte exact copy of a message cut out of an imported MBox file.</summary>
        public const string ImportMbox = "import-mbox";
        /// <summary>MIME re-serialized by MimeKit because the raw bytes were not available.</summary>
        public const string Serialized = "serialized";
        /// <summary>
        /// Email archived before originals were kept. The hash seals the stored fields at the
        /// time of sealing; there are no original bytes.
        /// </summary>
        public const string LegacySeal = "legacy-seal";
    }
}
