namespace MailArchiver.Models
{
    /// <summary>One run of the archive integrity verification for an organization.</summary>
    public class IntegrityCheckRun
    {
        public long Id { get; set; }
        public int OrganizationId { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string TriggeredBy { get; set; } = "system";

        /// <summary>Running, Ok, Failed (problems found) or Error (the check itself broke).</summary>
        public string Status { get; set; } = IntegrityCheckStatus.Running;

        public long OriginalsChecked { get; set; }
        public long OriginalsFailed { get; set; }
        public long AuditEntriesChecked { get; set; }
        public long AuditEntriesFailed { get; set; }
        public long AnchorsChecked { get; set; }
        public long AnchorsFailed { get; set; }

        /// <summary>Head of the originals chain at the end of the run.</summary>
        public long OriginalsHeadSeq { get; set; }
        public string? OriginalsHeadHash { get; set; }

        /// <summary>JSON array with the first problems found (bounded).</summary>
        public string? Problems { get; set; }
    }

    public static class IntegrityCheckStatus
    {
        public const string Running = "Running";
        public const string Ok = "Ok";
        public const string Failed = "Failed";
        public const string Error = "Error";
    }

    /// <summary>
    /// A snapshot of a chain head, written to WORM storage (and optionally time stamped
    /// by an RFC 3161 time stamp authority). Rewriting a chain afterwards no longer matches
    /// the anchor, so anchors turn "tamper-evident inside the database" into evidence that
    /// holds against an administrator of the database as well.
    /// </summary>
    public class IntegrityAnchor
    {
        public long Id { get; set; }
        public int OrganizationId { get; set; }

        /// <summary>"originals" or "audit".</summary>
        public string ChainKind { get; set; } = IntegrityChainKinds.Originals;

        public long ChainSeq { get; set; }
        public string ChainHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Object key of the anchor document in the WORM store, if written there.</summary>
        public string? StorageKey { get; set; }
        public string? StorageVersionId { get; set; }

        /// <summary>DER encoded RFC 3161 time stamp token over the anchor document, if any.</summary>
        public byte[]? TimestampToken { get; set; }
        public string? TimestampAuthority { get; set; }
    }

    public static class IntegrityChainKinds
    {
        public const string Originals = "originals";
        public const string Audit = "audit";
    }
}
