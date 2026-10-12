namespace MailArchiver.Services.Organizations
{
    /// <summary>A background job that belongs to the organization it was started in.</summary>
    public interface IOrganizationScopedJob
    {
        /// <summary>
        /// Organization of whoever queued the job; null for jobs started by the system
        /// (scheduler, CLI), -1 for a user without organization.
        /// </summary>
        int? OrganizationId { get; set; }
    }

    /// <summary>
    /// The in-memory jobs (exports, restores, imports, deletions, syncs) are kept in singleton
    /// services and processed in system scope. They carry the organization they were queued in,
    /// and every page that lists, shows, downloads or cancels a job checks it.
    /// </summary>
    public static class JobOrganization
    {
        /// <summary>
        /// The organization of the current request, read when a job object is created (the job
        /// models initialize their OrganizationId with it, so no call site has to remember).
        /// HttpContextAccessor keeps the context in an AsyncLocal, so a fresh instance sees it.
        /// </summary>
        public static int? Current()
        {
            var scope = new HttpOrganizationContext(new HttpContextAccessor());
            if (scope.IsSystem)
                return null;
            return scope.OrganizationId ?? -1;
        }

        public static bool IsVisible(IOrganizationContext scope, int? jobOrganizationId) =>
            scope.IsSystem || (jobOrganizationId != null && jobOrganizationId == scope.OrganizationId);

        /// <summary>The job if the current scope may see it, otherwise null (as if it did not exist).</summary>
        public static T? VisibleIn<T>(this T? job, IOrganizationContext scope) where T : class, IOrganizationScopedJob =>
            job != null && IsVisible(scope, job.OrganizationId) ? job : null;

        public static List<T> VisibleIn<T>(this IEnumerable<T> jobs, IOrganizationContext scope) where T : IOrganizationScopedJob =>
            jobs.Where(j => IsVisible(scope, j.OrganizationId)).ToList();
    }
}
