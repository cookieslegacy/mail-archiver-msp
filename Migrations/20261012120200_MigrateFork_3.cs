using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateFork_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Retention periods and legal hold, enforced by the database
            // ============================================================
            // Every email gets RetainUntil on insert: the end of the calendar
            // year of the email plus the retention years of its account (or,
            // when the account has none, of its organization), following
            // § 147 Abs. 4 AO. The year is taken from the later of SentDate
            // and ReceivedDate so a wrong Date header can only lengthen it.
            // The period ends at 01.01. 00:00 UTC of the following year, which
            // is never earlier than midnight in Germany. 0 years means no
            // retention obligation.
            //
            // Deleting an email - by any path: manual, local retention,
            // account deletion via cascade, raw SQL - fails with
            //   MA001  retention not yet expired
            //   MA002  legal hold on the email, its account or organization
            // RetainUntil can be extended (legal reasons, longer policy) but
            // never shortened. Emails archived before this migration have no
            // RetainUntil yet; the delete check computes it on the fly until
            // RetentionBackfillService has stored it.
            //
            // This works alongside the existing IsLocked / DeletionPolicy
            // mechanism of upstream, which stays unchanged.

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.ma_retention_end(
                    sent timestamp, received timestamp, years integer)
                RETURNS timestamp with time zone AS $$
                DECLARE
                    y integer;
                BEGIN
                    IF years IS NULL OR years <= 0 THEN
                        RETURN '-infinity'::timestamptz;
                    END IF;
                    y := extract(year FROM GREATEST(COALESCE(sent, received), COALESCE(received, sent)))::integer;
                    IF y IS NULL THEN
                        RETURN 'infinity'::timestamptz;
                    END IF;
                    IF y + years + 1 > 9999 THEN
                        RETURN 'infinity'::timestamptz;
                    END IF;
                    RETURN make_timestamptz(y + years + 1, 1, 1, 0, 0, 0, 'UTC');
                END;
                $$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION mail_archiver.ma_retention_years(account_id integer)
                RETURNS integer AS $$
                    SELECT COALESCE(a.""RetentionYears"", o.""RetentionYears"")
                    FROM mail_archiver.""MailAccounts"" a
                    JOIN mail_archiver.""Organizations"" o ON o.""Id"" = a.""OrganizationId""
                    WHERE a.""Id"" = account_id;
                $$ LANGUAGE sql STABLE;

                -- Retention in force for an email row (stored, or computed for old rows).
                CREATE OR REPLACE FUNCTION mail_archiver.ma_effective_retention(e mail_archiver.""ArchivedEmails"")
                RETURNS timestamp with time zone AS $$
                    SELECT COALESCE(e.""RetainUntil"",
                        mail_archiver.ma_retention_end(e.""SentDate"", e.""ReceivedDate"",
                            mail_archiver.ma_retention_years(e.""MailAccountId"")));
                $$ LANGUAGE sql STABLE;
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.ma_email_retention_insert()
                RETURNS TRIGGER AS $$
                DECLARE
                    years integer;
                    computed timestamptz;
                BEGIN
                    years := mail_archiver.ma_retention_years(NEW.""MailAccountId"");
                    IF years IS NULL OR years <= 0 THEN
                        computed := now();
                    ELSE
                        computed := mail_archiver.ma_retention_end(NEW.""SentDate"", NEW.""ReceivedDate"", years);
                    END IF;
                    -- A caller may ask for a longer retention, never for a shorter one.
                    NEW.""RetainUntil"" := GREATEST(COALESCE(NEW.""RetainUntil"", computed), computed);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                -- Fires after ma_email_organization (alphabetical order).
                DROP TRIGGER IF EXISTS ma_email_retention_insert ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_retention_insert
                    BEFORE INSERT ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.ma_email_retention_insert();

                CREATE OR REPLACE FUNCTION mail_archiver.ma_email_retention_update()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.""RetainUntil"" IS NULL
                       OR NEW.""RetainUntil"" < mail_archiver.ma_effective_retention(OLD) THEN
                        RAISE EXCEPTION USING ERRCODE = 'MA003',
                            MESSAGE = format('The retention of email %s can be extended but not shortened', OLD.""Id"");
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS ma_email_retention_update ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_retention_update
                    BEFORE UPDATE OF ""RetainUntil"" ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW
                    WHEN (NEW.""RetainUntil"" IS DISTINCT FROM OLD.""RetainUntil"")
                    EXECUTE FUNCTION mail_archiver.ma_email_retention_update();

                -- Keep the original's copy of the retention in step when it is extended.
                CREATE OR REPLACE FUNCTION mail_archiver.ma_email_retention_propagate()
                RETURNS TRIGGER AS $$
                BEGIN
                    UPDATE mail_archiver.""ArchivedEmailOriginals""
                    SET ""RetainUntil"" = NEW.""RetainUntil""
                    WHERE ""ArchivedEmailId"" = NEW.""Id""
                      AND ""PurgedAt"" IS NULL
                      AND (""RetainUntil"" IS NULL OR ""RetainUntil"" < NEW.""RetainUntil"");
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS ma_email_retention_propagate ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_retention_propagate
                    AFTER UPDATE OF ""RetainUntil"" ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW
                    WHEN (NEW.""RetainUntil"" IS DISTINCT FROM OLD.""RetainUntil"")
                    EXECUTE FUNCTION mail_archiver.ma_email_retention_propagate();

                CREATE OR REPLACE FUNCTION mail_archiver.ma_email_retention_delete()
                RETURNS TRIGGER AS $$
                DECLARE
                    retain timestamptz;
                BEGIN
                    retain := mail_archiver.ma_effective_retention(OLD);
                    IF retain > now() THEN
                        RAISE EXCEPTION USING ERRCODE = 'MA001',
                            MESSAGE = format('Retention of email %s runs until %s', OLD.""Id"", retain),
                            HINT = 'Emails can be deleted once their legal retention period has expired.';
                    END IF;
                    IF mail_archiver.ma_is_on_legal_hold(OLD.""Id"", OLD.""MailAccountId"", OLD.""OrganizationId"") THEN
                        RAISE EXCEPTION USING ERRCODE = 'MA002',
                            MESSAGE = format('Email %s is on legal hold', OLD.""Id""),
                            HINT = 'Release the legal hold of the email, its mail account or organization first.';
                    END IF;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS ma_email_retention_delete ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_retention_delete
                    BEFORE DELETE ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.ma_email_retention_delete();

                -- A deleted email takes the bytes of its original with it (data
                -- protection); the original's row stays as proof in the chain.
                -- Callers can name the reason in mail_archiver.purge_reason.
                CREATE OR REPLACE FUNCTION mail_archiver.ma_email_purge_original()
                RETURNS TRIGGER AS $$
                BEGIN
                    UPDATE mail_archiver.""ArchivedEmailOriginals""
                    SET ""RawMime"" = NULL,
                        ""PurgedAt"" = now(),
                        ""PurgeReason"" = COALESCE(NULLIF(current_setting('mail_archiver.purge_reason', true), ''), 'Email deleted')
                    WHERE ""ArchivedEmailId"" = OLD.""Id"" AND ""PurgedAt"" IS NULL;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS ma_email_purge_original ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_purge_original
                    AFTER DELETE ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.ma_email_purge_original();

                -- TRUNCATE skips row triggers, so it is refused altogether.
                CREATE OR REPLACE FUNCTION mail_archiver.ma_no_truncate()
                RETURNS TRIGGER AS $$
                BEGIN
                    RAISE EXCEPTION 'TRUNCATE of % is not allowed (retention)', TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS ma_email_no_truncate ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER ma_email_no_truncate
                    BEFORE TRUNCATE ON mail_archiver.""ArchivedEmails""
                    FOR EACH STATEMENT EXECUTE FUNCTION mail_archiver.ma_no_truncate();

                CREATE INDEX IF NOT EXISTS ""IX_ArchivedEmails_RetainUntil""
                    ON mail_archiver.""ArchivedEmails"" (""OrganizationId"", ""RetainUntil"");
                CREATE INDEX IF NOT EXISTS ""IX_ArchivedEmails_RetainUntil_Missing""
                    ON mail_archiver.""ArchivedEmails"" (""Id"") WHERE ""RetainUntil"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS ma_email_retention_insert ON mail_archiver.""ArchivedEmails"";
                DROP TRIGGER IF EXISTS ma_email_retention_update ON mail_archiver.""ArchivedEmails"";
                DROP TRIGGER IF EXISTS ma_email_retention_propagate ON mail_archiver.""ArchivedEmails"";
                DROP TRIGGER IF EXISTS ma_email_retention_delete ON mail_archiver.""ArchivedEmails"";
                DROP TRIGGER IF EXISTS ma_email_no_truncate ON mail_archiver.""ArchivedEmails"";
                DROP TRIGGER IF EXISTS ma_email_purge_original ON mail_archiver.""ArchivedEmails"";
                DROP FUNCTION IF EXISTS mail_archiver.ma_email_purge_original();
                DROP INDEX IF EXISTS mail_archiver.""IX_ArchivedEmails_RetainUntil"";
                DROP INDEX IF EXISTS mail_archiver.""IX_ArchivedEmails_RetainUntil_Missing"";
                DROP FUNCTION IF EXISTS mail_archiver.ma_email_retention_insert();
                DROP FUNCTION IF EXISTS mail_archiver.ma_email_retention_update();
                DROP FUNCTION IF EXISTS mail_archiver.ma_email_retention_propagate();
                DROP FUNCTION IF EXISTS mail_archiver.ma_email_retention_delete();
                DROP FUNCTION IF EXISTS mail_archiver.ma_no_truncate();
                DROP FUNCTION IF EXISTS mail_archiver.ma_effective_retention(mail_archiver.""ArchivedEmails"");
                DROP FUNCTION IF EXISTS mail_archiver.ma_retention_years(integer);
                DROP FUNCTION IF EXISTS mail_archiver.ma_retention_end(timestamp, timestamp, integer);
            ");
        }
    }
}
