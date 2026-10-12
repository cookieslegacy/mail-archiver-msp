using MailArchiver.Models;
using MailArchiver.Services.Organizations;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Data
{
    /// <summary>
    /// Organization (tenant) separation and the compliance tables.
    ///
    /// Every query on organization data is confined by a global query filter to the
    /// organization of the current user (see <see cref="IOrganizationContext"/>). Background work
    /// runs as "system" and is not filtered. The same boundary is enforced a second time by
    /// PostgreSQL row level security (see <see cref="OrganizationConnectionInterceptor"/>), which
    /// also covers raw SQL that bypasses the EF filters.
    /// </summary>
    public partial class MailArchiverDbContext
    {
        private readonly IOrganizationContext _organizationContext = SystemOrganizationContext.Instance;

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<ArchivedEmailOriginal> ArchivedEmailOriginals { get; set; }
        public DbSet<IntegrityCheckRun> IntegrityCheckRuns { get; set; }
        public DbSet<IntegrityAnchor> IntegrityAnchors { get; set; }

        /// <summary>The organization scope this context instance works in.</summary>
        public IOrganizationContext OrganizationScope => _organizationContext;

        // Inputs of the global query filters. EF reads these members of the context instance
        // on every query execution, so a context always filters by its current scope.
        private bool OrganizationFilterBypass => _organizationContext.IsSystem;
        private int OrganizationFilterId => _organizationContext.OrganizationId ?? -1;

        // Users and API keys must be found before anyone is signed in (login, API key
        // authentication). Provider admins see the users of their own organization only; the
        // admins of other organizations are managed on the organizations page, which writes
        // every change into that organization's audit log.
        private bool IdentityFilterBypass =>
            _organizationContext.IsSystem || _organizationContext.IsAnonymous;

        private void ConfigureOrganizationsAndCompliance(ModelBuilder modelBuilder)
        {
            // ---------------------------------------------------------------
            // Organizations
            // ---------------------------------------------------------------
            modelBuilder.Entity<Organization>(b =>
            {
                b.ToTable("Organizations", "mail_archiver");
                b.Property(o => o.Name).HasColumnType("text");
                b.Property(o => o.Slug).HasMaxLength(64);
                b.HasIndex(o => o.Slug).IsUnique();
                b.Property(o => o.EmailDomains).HasColumnType("text");
                b.Property(o => o.LegalHoldReason).HasColumnType("text");
                b.Property(o => o.Notes).HasColumnType("text");
                b.Property(o => o.CreatedAt).HasColumnType("timestamp with time zone");
                b.Ignore(o => o.EmailDomainList);
            });

            modelBuilder.Entity<User>()
                .HasOne(u => u.Organization)
                .WithMany()
                .HasForeignKey(u => u.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MailAccount>()
                .HasOne(a => a.Organization)
                .WithMany()
                .HasForeignKey(a => a.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Filled in by database triggers from the parent row; EF omits the column on insert
            // while it holds the CLR default and reads the trigger's value back.
            modelBuilder.Entity<ArchivedEmail>().Property(e => e.OrganizationId).ValueGeneratedOnAdd();
            modelBuilder.Entity<ArchivedEmail>().Property(e => e.RetainUntil)
                .HasColumnType("timestamp with time zone").ValueGeneratedOnAdd();
            modelBuilder.Entity<ArchivedEmail>().HasIndex(e => e.OrganizationId);
            modelBuilder.Entity<EmailAttachment>().Property(a => a.OrganizationId).ValueGeneratedOnAdd();
            modelBuilder.Entity<AccessLog>().Property(a => a.OrganizationId).ValueGeneratedOnAdd();
            modelBuilder.Entity<AccessLog>().Property(a => a.ChainSeq).ValueGeneratedOnAdd();
            modelBuilder.Entity<AccessLog>().Property(a => a.PrevChainHash).HasColumnType("char(64)").ValueGeneratedOnAdd();
            modelBuilder.Entity<AccessLog>().Property(a => a.ChainHash).HasColumnType("char(64)").ValueGeneratedOnAdd();

            // Attachment payloads are deduplicated per organization.
            modelBuilder.Entity<AttachmentContent>().HasIndex(c => c.Hash).IsUnique(false);
            modelBuilder.Entity<AttachmentContent>()
                .HasIndex(c => new { c.OrganizationId, c.Hash })
                .IsUnique()
                .HasDatabaseName("IX_AttachmentContents_Org_Hash");

            // ---------------------------------------------------------------
            // Originals and integrity records
            // ---------------------------------------------------------------
            modelBuilder.Entity<ArchivedEmailOriginal>(b =>
            {
                b.ToTable("ArchivedEmailOriginals", "mail_archiver");
                b.Property(o => o.MessageId).HasColumnType("text");
                b.Property(o => o.Source).HasMaxLength(20);
                b.Property(o => o.ContentSha256).HasColumnType("char(64)");
                b.Property(o => o.RawMime).HasColumnType("bytea");
                b.Property(o => o.StorageKey).HasColumnType("text");
                b.Property(o => o.StorageVersionId).HasColumnType("text");
                b.Property(o => o.PurgeReason).HasColumnType("text");
                b.Property(o => o.RetainUntil).HasColumnType("timestamp with time zone");
                b.Property(o => o.PurgedAt).HasColumnType("timestamp with time zone");
                // Computed by the chain trigger.
                b.Property(o => o.ArchivedAt).HasColumnType("timestamp with time zone").ValueGeneratedOnAdd();
                b.Property(o => o.ChainSeq).ValueGeneratedOnAdd();
                b.Property(o => o.PrevChainHash).HasColumnType("char(64)").ValueGeneratedOnAdd();
                b.Property(o => o.ChainHash).HasColumnType("char(64)").ValueGeneratedOnAdd();
                b.Property(o => o.OrganizationId).ValueGeneratedOnAdd();
                b.HasIndex(o => new { o.OrganizationId, o.ChainSeq }).IsUnique();
                b.HasIndex(o => o.ArchivedEmailId).IsUnique();
                // No database foreign key: the original outlives the email row as proof of the
                // deletion. ClientNoAction keeps EF from touching it when an email is deleted.
                b.HasOne(o => o.ArchivedEmail)
                    .WithOne(e => e.Original)
                    .HasForeignKey<ArchivedEmailOriginal>(o => o.ArchivedEmailId)
                    .OnDelete(DeleteBehavior.ClientNoAction);
            });

            modelBuilder.Entity<IntegrityCheckRun>(b =>
            {
                b.ToTable("IntegrityCheckRuns", "mail_archiver");
                b.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
                b.Property(r => r.FinishedAt).HasColumnType("timestamp with time zone");
                b.Property(r => r.TriggeredBy).HasColumnType("text");
                b.Property(r => r.Status).HasMaxLength(10);
                b.Property(r => r.OriginalsHeadHash).HasColumnType("char(64)");
                b.Property(r => r.Problems).HasColumnType("jsonb");
                b.HasIndex(r => new { r.OrganizationId, r.StartedAt });
            });

            modelBuilder.Entity<IntegrityAnchor>(b =>
            {
                b.ToTable("IntegrityAnchors", "mail_archiver");
                b.Property(a => a.ChainKind).HasMaxLength(16);
                b.Property(a => a.ChainHash).HasColumnType("char(64)");
                b.Property(a => a.CreatedAt).HasColumnType("timestamp with time zone");
                b.Property(a => a.StorageKey).HasColumnType("text");
                b.Property(a => a.StorageVersionId).HasColumnType("text");
                b.Property(a => a.TimestampToken).HasColumnType("bytea");
                b.Property(a => a.TimestampAuthority).HasColumnType("text");
                b.HasIndex(a => new { a.OrganizationId, a.ChainKind, a.ChainSeq });
            });

            // ---------------------------------------------------------------
            // Global query filters: one organization per user
            // ---------------------------------------------------------------
            modelBuilder.Entity<MailAccount>().HasQueryFilter(a =>
                OrganizationFilterBypass || a.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<ArchivedEmail>().HasQueryFilter(e =>
                OrganizationFilterBypass || e.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<EmailAttachment>().HasQueryFilter(a =>
                OrganizationFilterBypass || a.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<AttachmentContent>().HasQueryFilter(c =>
                OrganizationFilterBypass || c.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<ArchivedEmailOriginal>().HasQueryFilter(o =>
                OrganizationFilterBypass || o.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<IntegrityCheckRun>().HasQueryFilter(r =>
                OrganizationFilterBypass || r.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<IntegrityAnchor>().HasQueryFilter(a =>
                OrganizationFilterBypass || a.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<AccessLog>().HasQueryFilter(l =>
                OrganizationFilterBypass || l.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<AuditExportJob>().HasQueryFilter(j =>
                OrganizationFilterBypass || j.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<UserMailAccount>().HasQueryFilter(m =>
                OrganizationFilterBypass || m.MailAccount.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<BandwidthUsage>().HasQueryFilter(b =>
                OrganizationFilterBypass || b.MailAccount.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<SyncCheckpoint>().HasQueryFilter(s =>
                OrganizationFilterBypass || s.MailAccount.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<AccountStorageCache>().HasQueryFilter(c =>
                OrganizationFilterBypass || c.MailAccount.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<AccountStorageBackfillState>().HasQueryFilter(s =>
                OrganizationFilterBypass || s.MailAccount.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<User>().HasQueryFilter(u =>
                IdentityFilterBypass || u.OrganizationId == OrganizationFilterId);
            modelBuilder.Entity<ApiKey>().HasQueryFilter(k =>
                IdentityFilterBypass || k.User.OrganizationId == OrganizationFilterId);
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            AssignOrganizations();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            AssignOrganizations();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// New mail accounts, users and export jobs inherit the organization of whoever creates
        /// them, so none of the existing create paths has to know about organizations. Work
        /// without a user (system) falls back to the default organization.
        /// </summary>
        private void AssignOrganizations()
        {
            int? scopeOrganization = _organizationContext.OrganizationId;

            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State != EntityState.Added)
                    continue;

                switch (entry.Entity)
                {
                    case MailAccount account when account.OrganizationId == 0:
                        account.OrganizationId = scopeOrganization
                            ?? (_organizationContext.IsSystem
                                ? Organization.DefaultOrganizationId
                                : throw new InvalidOperationException(
                                    "A mail account can only be created inside an organization."));
                        break;

                    case User user when user.OrganizationId == null && !user.IsProviderAdmin:
                        user.OrganizationId = scopeOrganization
                            ?? (_organizationContext.IsSystem ? Organization.DefaultOrganizationId : null);
                        break;

                    // A job created by a user without organization gets -1 and exports nothing;
                    // only jobs created by system work stay unrestricted (null).
                    case AuditExportJob job when job.OrganizationId == null && !_organizationContext.IsSystem:
                        job.OrganizationId = scopeOrganization ?? -1;
                        break;

                    case AccessLog log when log.OrganizationId == null && !_organizationContext.IsSystem:
                        log.OrganizationId = scopeOrganization;
                        break;
                }
            }
        }
    }
}
