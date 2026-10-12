namespace MailArchiver.Models
{
    public class MailAccountDeletionJob : MailArchiver.Services.Organizations.IOrganizationScopedJob
    {
        // Organization the job was queued in (see JobOrganization); checked by every job page.
        public int? OrganizationId { get; set; } = MailArchiver.Services.Organizations.JobOrganization.Current();

        public string JobId { get; set; } = Guid.NewGuid().ToString();
        public int MailAccountId { get; set; }
        public string MailAccountName { get; set; } = string.Empty;
        public string UserId { get; set; } = "System";
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime? Started { get; set; }
        public DateTime? Completed { get; set; }
        public MailAccountDeletionJobStatus Status { get; set; } = MailAccountDeletionJobStatus.Queued;
        public int DeletedAttachments { get; set; }
        public int DeletedEmails { get; set; }
        public int TotalAttachments { get; set; }
        public int TotalEmails { get; set; }
        public string? ErrorMessage { get; set; }
        public string CurrentPhase { get; set; } = "Initializing";
        public bool IsCompleted => Status == MailAccountDeletionJobStatus.Completed || 
                                   Status == MailAccountDeletionJobStatus.Failed || 
                                   Status == MailAccountDeletionJobStatus.Cancelled;
    }

    public enum MailAccountDeletionJobStatus
    {
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled
    }
}
