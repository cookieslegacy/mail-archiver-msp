using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models
{
    /// <summary>
    /// A customer organization (tenant / "Mandant"). Every mail account, archived email,
    /// attachment, original and audit entry belongs to exactly one organization, and users of
    /// one organization never see data of another one.
    ///
    /// Named Organization rather than Tenant because <see cref="MailAccount.TenantId"/> already
    /// holds the Microsoft Entra tenant of an M365 account.
    /// </summary>
    public class Organization
    {
        /// <summary>The organization that existing data is moved into on upgrade.</summary>
        public const int DefaultOrganizationId = 1;

        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        /// <summary>Short unique key, used in storage paths (e.g. the WORM object prefix).</summary>
        [Required]
        [StringLength(64)]
        [RegularExpression("^[a-z0-9][a-z0-9-]*$")]
        public string Slug { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Comma separated mail domains. Users signing in through OIDC whose address belongs to
        /// one of these domains are created inside this organization.
        /// </summary>
        public string EmailDomains { get; set; } = string.Empty;

        /// <summary>
        /// Default retention in years. The period ends with the end of the calendar year
        /// (§ 147 Abs. 4 AO), so an email of 2026 with 10 years is kept until 31.12.2036.
        /// A mail account can override it.
        /// </summary>
        [Range(0, 100)]
        public int RetentionYears { get; set; } = 10;

        /// <summary>Delete emails automatically once their retention has expired.</summary>
        public bool AutoPurgeExpired { get; set; }

        /// <summary>Legal hold for the whole organization: nothing is deleted while set.</summary>
        public bool LegalHold { get; set; }

        public string? LegalHoldReason { get; set; }

        /// <summary>
        /// Set by the organization's own admins. Only while it is set can a provider admin open
        /// this organization's data for support, and every such access is written to the
        /// organization's audit log.
        /// </summary>
        public bool AllowProviderAccess { get; set; }

        /// <summary>The booked package (storage, number of mail accounts); null = no limits.</summary>
        public int? PackageId { get; set; }
        public virtual Package? Package { get; set; }

        /// <summary>Individual storage quota in bytes; overrides the package when set.</summary>
        public long? StorageQuotaBytes { get; set; }

        /// <summary>Individual limit of mail accounts; overrides the package when set.</summary>
        public int? MaxMailAccounts { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string? Notes { get; set; }

        public long? EffectiveStorageQuotaBytes => StorageQuotaBytes ?? Package?.StorageQuotaBytes;
        public int? EffectiveMaxMailAccounts => MaxMailAccounts ?? Package?.MaxMailAccounts;
        public bool BlockArchivingOverQuota => Package?.BlockArchivingOverQuota == true;

        public IEnumerable<string> EmailDomainList => EmailDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant());
    }
}
