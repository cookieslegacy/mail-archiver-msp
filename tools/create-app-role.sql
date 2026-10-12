-- Makes PostgreSQL row level security effective for Mail-Archiver.
--
-- Row level security never applies to superusers or roles with BYPASSRLS, and the
-- default docker-compose setup connects as the superuser created by the postgres
-- image. This script creates a separate role without those rights, makes it the
-- owner of everything in the mail_archiver schema (the policies use FORCE ROW LEVEL
-- SECURITY, so they bind the owner too) and lets the application connect with it.
--
-- Run it once as the superuser, after the application has started at least once
-- (so the schema exists), then change the connection string to the new role:
--
--   docker compose exec -T postgres psql -U mailuser -d MailArchiver \
--       -v app_password="'a-long-random-password'" -f - < tools/create-app-role.sql
--
--   ConnectionStrings__DefaultConnection=Host=postgres;Database=MailArchiver;Username=mailarchiver_app;Password=...
--
-- The extensions pgcrypto and citext must already exist (the application creates
-- them on first start as superuser); the new role cannot create extensions.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mailarchiver_app') THEN
        CREATE ROLE mailarchiver_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB;
    END IF;
END $$;

ALTER ROLE mailarchiver_app PASSWORD :app_password;
ALTER ROLE mailarchiver_app NOSUPERUSER NOBYPASSRLS;

GRANT CONNECT, TEMPORARY ON DATABASE :"DBNAME" TO mailarchiver_app;
ALTER SCHEMA mail_archiver OWNER TO mailarchiver_app;

DO $$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'mail_archiver' LOOP
        EXECUTE format('ALTER TABLE mail_archiver.%I OWNER TO mailarchiver_app', r.tablename);
    END LOOP;
    FOR r IN SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'mail_archiver' LOOP
        EXECUTE format('ALTER SEQUENCE mail_archiver.%I OWNER TO mailarchiver_app', r.sequence_name);
    END LOOP;
    FOR r IN SELECT p.oid::regprocedure AS sig FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'mail_archiver' AND p.prokind IN ('f', 'p') LOOP
        EXECUTE format('ALTER ROUTINE %s OWNER TO mailarchiver_app', r.sig);
    END LOOP;
END $$;

-- EF Core keeps its migration history in the public schema.
ALTER TABLE IF EXISTS public."__EFMigrationsHistory" OWNER TO mailarchiver_app;
GRANT USAGE, CREATE ON SCHEMA public TO mailarchiver_app;

SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'mailarchiver_app';
