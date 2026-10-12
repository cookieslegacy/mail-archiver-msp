using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateFork_4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Tamper-evident audit log
            // ============================================================
            // AccessLogs becomes append-only and every entry is chained per
            // organization (provider level entries without organization form
            // chain 0), exactly like the originals:
            //
            //   ChainHash = sha256(canonical(entry, PrevChainHash))
            //
            // Free text fields are length prefixed (UTF-8 bytes) in the
            // canonical form so no combination of field values can produce
            // the input of another one. Timestamp is a timestamp without time
            // zone holding UTC; its epoch is taken as is, independent of the
            // session time zone.
            //
            // Existing entries are chained once, in Id order, before the
            // append-only protection is switched on.

            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""AccessLogs""
                    ADD COLUMN IF NOT EXISTS ""ChainSeq"" bigint NULL,
                    ADD COLUMN IF NOT EXISTS ""PrevChainHash"" char(64) NULL,
                    ADD COLUMN IF NOT EXISTS ""ChainHash"" char(64) NULL;

                CREATE OR REPLACE FUNCTION mail_archiver.ma_epoch_micros(ts timestamp without time zone)
                RETURNS bigint AS $$
                    SELECT (extract(epoch FROM ts) * 1000000)::bigint;
                $$ LANGUAGE sql IMMUTABLE;

                CREATE OR REPLACE FUNCTION mail_archiver.ma_lp(value text)
                RETURNS text AS $$
                    SELECT CASE WHEN value IS NULL THEN '-' ELSE octet_length(value)::text || ':' || value END;
                $$ LANGUAGE sql IMMUTABLE;

                CREATE OR REPLACE FUNCTION mail_archiver.ma_audit_chain_input(l mail_archiver.""AccessLogs"")
                RETURNS text AS $$
                    SELECT concat_ws('|',
                        'MA-AUDIT-v1',
                        COALESCE(l.""OrganizationId"", 0)::text,
                        l.""ChainSeq""::text,
                        l.""PrevChainHash"",
                        l.""Id""::text,
                        mail_archiver.ma_lp(l.""Username""),
                        l.""Type""::text,
                        mail_archiver.ma_epoch_micros(l.""Timestamp"")::text,
                        COALESCE(l.""EmailId""::text, '-'),
                        mail_archiver.ma_lp(l.""EmailSubject""),
                        mail_archiver.ma_lp(l.""EmailFrom""),
                        mail_archiver.ma_lp(l.""SearchParameters""),
                        COALESCE(l.""MailAccountId""::text, '-'));
                $$ LANGUAGE sql IMMUTABLE;
            ");

            // Chain the existing entries (once).
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    r record;
                    chain integer := NULL;
                    seq bigint;
                    prev text;
                    row_hash text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM mail_archiver.""AccessLogs"" WHERE ""ChainHash"" IS NULL) THEN
                        FOR r IN
                            SELECT * FROM mail_archiver.""AccessLogs""
                            ORDER BY COALESCE(""OrganizationId"", 0), ""Id""
                        LOOP
                            IF chain IS DISTINCT FROM COALESCE(r.""OrganizationId"", 0) THEN
                                chain := COALESCE(r.""OrganizationId"", 0);
                                seq := 0;
                                prev := repeat('0', 64);
                            END IF;
                            seq := seq + 1;
                            r.""ChainSeq"" := seq;
                            r.""PrevChainHash"" := prev;
                            row_hash := encode(digest(mail_archiver.ma_audit_chain_input(
                                ROW(r.*)::mail_archiver.""AccessLogs""), 'sha256'), 'hex');
                            UPDATE mail_archiver.""AccessLogs""
                            SET ""ChainSeq"" = seq, ""PrevChainHash"" = prev, ""ChainHash"" = row_hash
                            WHERE ""Id"" = r.""Id"";
                            prev := row_hash;
                        END LOOP;
                    END IF;
                END $$;

                ALTER TABLE mail_archiver.""AccessLogs""
                    ALTER COLUMN ""ChainSeq"" SET NOT NULL,
                    ALTER COLUMN ""PrevChainHash"" SET NOT NULL,
                    ALTER COLUMN ""ChainHash"" SET NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AccessLogs_Chain""
                    ON mail_archiver.""AccessLogs"" ((COALESCE(""OrganizationId"", 0)), ""ChainSeq"");
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.ma_access_log_chain()
                RETURNS TRIGGER AS $$
                DECLARE
                    chain integer;
                    last_seq bigint;
                    last_hash text;
                BEGIN
                    chain := COALESCE(NEW.""OrganizationId"", 0);
                    PERFORM pg_advisory_xact_lock(1096107348, chain);

                    SELECT ""ChainSeq"", ""ChainHash"" INTO last_seq, last_hash
                    FROM mail_archiver.""AccessLogs""
                    WHERE COALESCE(""OrganizationId"", 0) = chain
                    ORDER BY ""ChainSeq"" DESC
                    LIMIT 1;

                    NEW.""ChainSeq"" := COALESCE(last_seq, 0) + 1;
                    NEW.""PrevChainHash"" := COALESCE(last_hash, repeat('0', 64));
                    NEW.""ChainHash"" := encode(digest(mail_archiver.ma_audit_chain_input(NEW), 'sha256'), 'hex');
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                -- Runs after ma_access_log_a_organization (alphabetical order).
                DROP TRIGGER IF EXISTS ma_access_log_b_chain ON mail_archiver.""AccessLogs"";
                CREATE TRIGGER ma_access_log_b_chain
                    BEFORE INSERT ON mail_archiver.""AccessLogs""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.ma_access_log_chain();

                DROP TRIGGER IF EXISTS ma_access_log_append_only ON mail_archiver.""AccessLogs"";
                CREATE TRIGGER ma_access_log_append_only
                    BEFORE UPDATE OR DELETE ON mail_archiver.""AccessLogs""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.ma_append_only();

                DROP TRIGGER IF EXISTS ma_access_log_no_truncate ON mail_archiver.""AccessLogs"";
                CREATE TRIGGER ma_access_log_no_truncate
                    BEFORE TRUNCATE ON mail_archiver.""AccessLogs""
                    FOR EACH STATEMENT EXECUTE FUNCTION mail_archiver.ma_append_only();

                COMMENT ON TABLE mail_archiver.""AccessLogs""
                    IS 'Audit log, append-only, chained per organization (SHA-256).';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS ma_access_log_b_chain ON mail_archiver.""AccessLogs"";
                DROP TRIGGER IF EXISTS ma_access_log_append_only ON mail_archiver.""AccessLogs"";
                DROP TRIGGER IF EXISTS ma_access_log_no_truncate ON mail_archiver.""AccessLogs"";
                DROP INDEX IF EXISTS mail_archiver.""IX_AccessLogs_Chain"";
                ALTER TABLE mail_archiver.""AccessLogs""
                    DROP COLUMN IF EXISTS ""ChainSeq"",
                    DROP COLUMN IF EXISTS ""PrevChainHash"",
                    DROP COLUMN IF EXISTS ""ChainHash"";
                DROP FUNCTION IF EXISTS mail_archiver.ma_access_log_chain();
                DROP FUNCTION IF EXISTS mail_archiver.ma_audit_chain_input(mail_archiver.""AccessLogs"");
                DROP FUNCTION IF EXISTS mail_archiver.ma_lp(text);
                DROP FUNCTION IF EXISTS mail_archiver.ma_epoch_micros(timestamp without time zone);
            ");
        }
    }
}
