using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models
{
    /// <summary>
    /// What the provider sells to an organization: an amount of storage and a number of mail
    /// accounts. An organization can override both values individually.
    /// </summary>
    public class Package
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>Storage quota in bytes; null = unlimited.</summary>
        public long? StorageQuotaBytes { get; set; }

        /// <summary>Maximum number of mail accounts; null = unlimited.</summary>
        public int? MaxMailAccounts { get; set; }

        /// <summary>
        /// Stop fetching new mail once the quota plus the grace margin is used up. Off: the
        /// quota only warns. Archiving resumes by itself when the quota is raised; mail that
        /// arrived meanwhile is fetched then, provided it is still on the mail server.
        /// </summary>
        public bool BlockArchivingOverQuota { get; set; }

        /// <summary>Inactive packages cannot be booked any more; existing bookings stay.</summary>
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Bytes of the originals of one mail account, maintained by database triggers.</summary>
    public class MailAccountUsage
    {
        public int MailAccountId { get; set; }
        public int OrganizationId { get; set; }
        public long OriginalBytes { get; set; }
        public long OriginalCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Usage of an organization at the end of a day, for billing and trends.</summary>
    public class OrganizationUsageSnapshot
    {
        public int OrganizationId { get; set; }
        public DateOnly Day { get; set; }
        public long StorageBytes { get; set; }
        public long OriginalBytes { get; set; }
        public int MailAccounts { get; set; }
        public long Emails { get; set; }
        public long? StorageQuotaBytes { get; set; }
        public string? PackageName { get; set; }
    }
}
