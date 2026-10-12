using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models.ViewModels
{
    public class CreateUserViewModel
    {
        [Required]
        [StringLength(50)]
        public string Username { get; set; }
        
        [Required]
        [StringLength(100)]
        public string Email { get; set; }
        
        [Required]
        public bool IsAdmin { get; set; } = false;
        
        public bool IsSelfManager { get; set; } = false;

        // Read access to every mail account of the organization (e.g. tax auditor)
        public bool IsAuditor { get; set; } = false;
        
        public bool IsActive { get; set; } = true;
    }
}
