namespace MailArchiver.Services.Compliance
{
    /// <summary>Where original messages are kept.</summary>
    public class ArchiveStorageOptions
    {
        public const string SectionName = "ArchiveStorage";

        /// <summary>"Database" (default) or "S3" (WORM storage with S3 Object Lock).</summary>
        public string Mode { get; set; } = "Database";

        /// <summary>In S3 mode, keep a second copy of each original in the database as well.</summary>
        public bool KeepDatabaseCopy { get; set; }

        public S3WormOptions S3 { get; set; } = new();

        public bool UsesWorm => string.Equals(Mode, "S3", StringComparison.OrdinalIgnoreCase);
    }

    public class S3WormOptions
    {
        /// <summary>Endpoint for S3 compatible services (MinIO, Wasabi, Hetzner, ...). Empty for AWS.</summary>
        public string ServiceUrl { get; set; } = string.Empty;
        public string Region { get; set; } = "us-east-1";
        public string Bucket { get; set; } = string.Empty;
        public string AccessKey { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
        public bool ForcePathStyle { get; set; } = true;

        /// <summary>
        /// COMPLIANCE (default): nobody, not even the bucket owner, can delete or shorten the
        /// retention of an object before it expires. GOVERNANCE: privileged users can, which is
        /// only suitable for testing.
        /// </summary>
        public string LockMode { get; set; } = "COMPLIANCE";

        /// <summary>Optional key prefix inside the bucket.</summary>
        public string Prefix { get; set; } = string.Empty;
    }

    /// <summary>Schedules and settings of the compliance background work.</summary>
    public class ComplianceOptions
    {
        public const string SectionName = "Compliance";

        /// <summary>Full integrity verification of every organization (0 = only on demand).</summary>
        public int VerifyIntervalHours { get; set; } = 168;

        /// <summary>How often the chain heads are anchored (0 = never).</summary>
        public int AnchorIntervalHours { get; set; } = 24;

        /// <summary>RFC 3161 time stamp authority for the anchors, e.g. https://freetsa.org/tsr. Empty = none.</summary>
        public string TimestampAuthorityUrl { get; set; } = string.Empty;

        /// <summary>How often expired emails are purged (organizations with AutoPurgeExpired only).</summary>
        public int PurgeIntervalHours { get; set; } = 24;

        /// <summary>Rows per batch for the retention backfill and the legacy sealing.</summary>
        public int BatchSize { get; set; } = 1000;

        /// <summary>Seal emails archived before originals were kept (see IntegrityChain.LegacySeal).</summary>
        public bool SealLegacyEmails { get; set; } = true;
    }
}
