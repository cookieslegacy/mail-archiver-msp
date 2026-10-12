using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models
{
    public class User
    {
        public int Id { get; set; }
        
        [Required]
        [StringLength(320)]
        public string Username { get; set; }
        
        [Required]
        [StringLength(100)]
        public string Email { get; set; }
        
        public string? PasswordHash { get; set; }
        
        [Required]
        public bool IsAdmin { get; set; } = false;
        
        [Required]
        public bool IsSelfManager { get; set; } = false;
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? LastLoginAt { get; set; }
        
        // 2FA TOTP properties
        public bool IsTwoFactorEnabled { get; set; } = false;
        public string? TwoFactorSecret { get; set; }
        public string? TwoFactorBackupCodes { get; set; }

        // OAuth properties
        public string? OAuthRemoteUserId { get; set; }
        public bool RequiresApproval { get; set; } = false;

        // Version update tracking
        public string? LastSeenChangelogVersion { get; set; }

        // Organization (tenant). Null only for provider admins without an own organization
        // and for users still waiting to be assigned to one.
        public int? OrganizationId { get; set; }
        public virtual Organization? Organization { get; set; }

        // Manages organizations and their admins (service provider staff).
        public bool IsProviderAdmin { get; set; } = false;

        // Read-only access to every mail account of the user's organization (e.g. tax auditor).
        public bool IsAuditor { get; set; } = false;
        
        // Navigation properties
        public virtual ICollection<UserMailAccount> UserMailAccounts { get; set; } = new List<UserMailAccount>();
        public virtual ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
    }
}
