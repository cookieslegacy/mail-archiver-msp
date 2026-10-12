using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models
{
    public class AccessLog
    {
        public int Id { get; set; }
        
        [Required]
        public string Username { get; set; }
        
        [Required]
        public AccessLogType Type { get; set; }
        
        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        // Optional email reference for actions related to specific emails
        public int? EmailId { get; set; }
        
        // Optional email subject for context
        public string? EmailSubject { get; set; }
        
        // Optional email sender for context
        public string? EmailFrom { get; set; }
        
        // Optional search parameters for search actions
        public string? SearchParameters { get; set; }
        
        // Optional email account for account-specific actions
        public int? MailAccountId { get; set; }

        // Organization (tenant); null for provider level actions. Filled by the database
        // from the account or user when not set.
        public int? OrganizationId { get; set; }

        // Position in the organization's audit hash chain, set by the database on insert.
        public long ChainSeq { get; set; }
        public string? PrevChainHash { get; set; }
        public string? ChainHash { get; set; }
    }
    
    public enum AccessLogType
    {
        Login,
        Logout,
        Search,
        Open,
        Download,
        Restore,
        Account,
        Deletion,
        DatabaseMaintenance,
        SyncCancel,
        DeletionPolicy,
        SyncAcknowledgeFailures,
        Retention,
        AuditExport,
        // Organization (tenant) and compliance events
        Organization,
        ProviderSupportAccess,
        LegalHold,
        IntegrityCheck,
        RetentionPurge
    }
}
