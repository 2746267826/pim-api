-- sql/ops-ro-grants.sql
-- Create read-only role for ops database access
-- Usage: psql -h <host> -U <superuser> -d pim -f sql/ops-ro-grants.sql
-- Requires: run as superuser or owner of database pim

-- Create role pim_ro if not exists (PostgreSQL 14+ supports IF NOT EXISTS via DO block for older compat)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pim_ro') THEN
        CREATE ROLE pim_ro NOLOGIN;
    END IF;
END
$$;

GRANT CONNECT ON DATABASE pim TO pim_ro;
GRANT USAGE ON SCHEMA public TO pim_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO pim_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO pim_ro;

-- Column-level REVOKE for sensitive columns (must run after GRANT)
-- Adjust table names as per actual schema; idempotent via DO blocks

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='users' AND column_name='password_hash') THEN
        REVOKE SELECT (password_hash) ON users FROM pim_ro;
    END IF;
END
$$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='refresh_tokens' AND column_name='token_hash') THEN
        REVOKE SELECT (token_hash) ON refresh_tokens FROM pim_ro;
    END IF;
END
$$;

-- Additional sensitive columns (if present)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='users' AND column_name='token_hash') THEN
        REVOKE SELECT (token_hash) ON users FROM pim_ro;
    END IF;
END
$$;

-- Example: revoke on other tables if they exist
-- REVOKE SELECT (password_hash) ON admin_users FROM pim_ro;
-- REVOKE SELECT (secret) ON api_keys FROM pim_ro;

-- Verify: should show pim_ro has SELECT but not on revoked columns
-- \z users
-- SELECT * FROM information_schema.role_column_grants WHERE grantee='pim_ro';
