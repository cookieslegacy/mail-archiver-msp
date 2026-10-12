using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailArchiver.Data
{
    /// <summary>
    /// Hands the organization scope of a <see cref="MailArchiverDbContext"/> to PostgreSQL every
    /// time EF opens a connection, as the setting <c>mail_archiver.org_id</c>. The row level
    /// security policies created by the MigrateFork_1 migration compare against it:
    ///
    ///   ''      system scope, every row is visible (background work, migrations)
    ///   '-1'    no organization, no organization data is visible
    ///   '&lt;id&gt;'  only rows of that organization are visible
    ///
    /// Row level security does not apply to PostgreSQL superusers. It only takes effect when the
    /// application connects with a role without SUPERUSER and BYPASSRLS (see docs).
    /// </summary>
    public class OrganizationConnectionInterceptor : DbConnectionInterceptor
    {
        public const string SettingName = "mail_archiver.org_id";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = BuildCommand(connection, eventData);
            command?.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = BuildCommand(connection, eventData);
            if (command != null)
                await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static DbCommand? BuildCommand(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (eventData.Context is not MailArchiverDbContext context)
                return null;

            var command = connection.CreateCommand();
            command.CommandText = $"SELECT set_config('{SettingName}', @org, false)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "org";
            parameter.Value = ScopeValue(context);
            command.Parameters.Add(parameter);
            return command;
        }

        internal static string ScopeValue(MailArchiverDbContext context)
        {
            var scope = context.OrganizationScope;
            if (scope.IsSystem)
                return string.Empty;
            return (scope.OrganizationId ?? -1).ToString();
        }
    }
}
