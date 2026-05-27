-- Radiology Plus v2.1 — Foundation migration
-- Creates: core, tenancy, identity, audit, scripting, notifications schemas.
-- Establishes multi-tenant model (tenant_id everywhere) and RLS policies.
--
-- Migrator wraps each file in a transaction; no BEGIN/COMMIT here.

-- ============================================================================
-- Required extensions
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "citext";

-- ============================================================================
-- Schemas
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS core;
CREATE SCHEMA IF NOT EXISTS tenancy;
CREATE SCHEMA IF NOT EXISTS identity;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS scripting;
CREATE SCHEMA IF NOT EXISTS notifications;

COMMENT ON SCHEMA core IS 'Cross-cutting infrastructure: migrations, system settings.';
COMMENT ON SCHEMA tenancy IS 'Tenant registry + per-tenant Novarad connection configuration.';
COMMENT ON SCHEMA identity IS 'Local NRS users + federated user cache.';
COMMENT ON SCHEMA audit IS 'Access log + application log sink.';
COMMENT ON SCHEMA scripting IS 'NRS script definitions, schedules, executions, chains.';
COMMENT ON SCHEMA notifications IS 'Outbound notification queue + delivery log.';

-- ============================================================================
-- Migration tracking
-- ============================================================================
CREATE TABLE IF NOT EXISTS core.schema_migrations (
    version       TEXT PRIMARY KEY,
    applied_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    checksum      TEXT NOT NULL,
    applied_by    TEXT NOT NULL DEFAULT CURRENT_USER
);

-- ============================================================================
-- Tenancy
-- ============================================================================
CREATE TABLE tenancy.tenants (
    tenant_id       UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    code            CITEXT NOT NULL UNIQUE,
    display_name    TEXT NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE tenancy.tenants IS 'One row per Novarad customer using Radiology Plus.';

CREATE TABLE tenancy.facilities (
    facility_id     SERIAL PRIMARY KEY,
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    novarad_facility_id INTEGER NOT NULL,
    code            TEXT NOT NULL,
    display_name    TEXT NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, novarad_facility_id)
);
CREATE INDEX idx_facilities_tenant ON tenancy.facilities(tenant_id);

CREATE TABLE tenancy.novarad_connections (
    tenant_id       UUID PRIMARY KEY REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    host            TEXT NOT NULL,
    port            INTEGER NOT NULL DEFAULT 5432,
    database_name   TEXT NOT NULL,
    username        TEXT NOT NULL,
    password_encrypted BYTEA NOT NULL,
    use_ssl         BOOLEAN NOT NULL DEFAULT TRUE,
    novarad_audit_table TEXT NOT NULL DEFAULT 'object_store.audit',
    notes           TEXT,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE tenancy.novarad_connections IS 'Per-tenant connection to Novarad over VPN. Password is symmetrically encrypted with the app master key.';

-- ============================================================================
-- Identity
-- ============================================================================
CREATE TABLE identity.users (
    user_id         UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    username        CITEXT NOT NULL,
    display_name    TEXT NOT NULL,
    email           CITEXT,
    role            SMALLINT NOT NULL CHECK (role IN (1, 2, 3, 4)),
    is_local        BOOLEAN NOT NULL DEFAULT FALSE,
    password_hash   TEXT,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    last_login_at   TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, username),
    CONSTRAINT chk_local_users_have_hash CHECK (
        (is_local = FALSE) OR (is_local = TRUE AND password_hash IS NOT NULL)
    )
);
COMMENT ON COLUMN identity.users.role IS '1=NRS, 2=Admin, 3=Tech, 4=Radiologist';
COMMENT ON COLUMN identity.users.is_local IS 'TRUE only for NRS rows. FALSE for federated Novarad users.';
CREATE INDEX idx_users_tenant ON identity.users(tenant_id);

CREATE TABLE identity.user_facilities (
    user_id         UUID NOT NULL REFERENCES identity.users(user_id) ON DELETE CASCADE,
    facility_id     INTEGER NOT NULL REFERENCES tenancy.facilities(facility_id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, facility_id)
);

CREATE TABLE identity.refresh_tokens (
    token_id        UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    user_id         UUID NOT NULL REFERENCES identity.users(user_id) ON DELETE CASCADE,
    token_hash      TEXT NOT NULL,
    expires_at      TIMESTAMPTZ NOT NULL,
    revoked_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (token_hash)
);
CREATE INDEX idx_refresh_tokens_user ON identity.refresh_tokens(user_id);

-- ============================================================================
-- Audit
-- ============================================================================
CREATE TABLE audit.access_logs (
    log_id          BIGSERIAL PRIMARY KEY,
    tenant_id       UUID NOT NULL,
    user_id         UUID,
    username        TEXT,
    action          SMALLINT NOT NULL,
    resource_type   TEXT NOT NULL,
    resource_id     TEXT,
    success         BOOLEAN NOT NULL,
    ip_address      INET,
    user_agent      TEXT,
    error_message   TEXT,
    metadata        JSONB,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_access_logs_tenant_time ON audit.access_logs(tenant_id, occurred_at DESC);
CREATE INDEX idx_access_logs_user_time ON audit.access_logs(user_id, occurred_at DESC);
CREATE INDEX idx_access_logs_resource ON audit.access_logs(resource_type, resource_id);
COMMENT ON TABLE audit.access_logs IS 'Append-only application audit. action: 1=Login 2=Logout 3=Read 4=Create 5=Update 6=Delete 7=Execute 8=NovaradWrite 9=PermissionDenied.';

CREATE TABLE audit.app_logs (
    log_id          BIGSERIAL PRIMARY KEY,
    tenant_id       UUID,
    level           TEXT NOT NULL,
    source          TEXT NOT NULL,
    message         TEXT NOT NULL,
    exception       TEXT,
    properties      JSONB,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_app_logs_time ON audit.app_logs(occurred_at DESC);
CREATE INDEX idx_app_logs_tenant_level ON audit.app_logs(tenant_id, level, occurred_at DESC);
COMMENT ON TABLE audit.app_logs IS 'Serilog Postgres sink target. Tenant-scoped where context is available.';

-- ============================================================================
-- Scripting (plumbing only — UI lands in Phase 4)
-- ============================================================================
CREATE TABLE scripting.scripts (
    script_id       UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    description     TEXT,
    language        TEXT NOT NULL CHECK (language IN ('tsql', 'pgsql', 'powershell', 'batch')),
    body            TEXT NOT NULL,
    cron_expression TEXT,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    timeout_seconds INTEGER NOT NULL DEFAULT 300,
    parameters_json JSONB,
    created_by      UUID,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, name)
);

CREATE TABLE scripting.script_versions (
    version_id      UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    script_id       UUID NOT NULL REFERENCES scripting.scripts(script_id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL,
    version_number  INTEGER NOT NULL,
    body            TEXT NOT NULL,
    saved_by        UUID,
    saved_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (script_id, version_number)
);

CREATE TABLE scripting.executions (
    execution_id    BIGSERIAL PRIMARY KEY,
    script_id       UUID NOT NULL REFERENCES scripting.scripts(script_id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL,
    triggered_by    TEXT NOT NULL CHECK (triggered_by IN ('schedule', 'manual', 'chain', 'event')),
    triggered_by_user UUID,
    status          TEXT NOT NULL CHECK (status IN ('pending', 'running', 'success', 'failed', 'cancelled')),
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    duration_ms     INTEGER,
    exit_code       INTEGER,
    output_log      TEXT,
    error_log       TEXT,
    rows_affected   INTEGER,
    parameters_used JSONB,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_executions_script_time ON scripting.executions(script_id, started_at DESC);
CREATE INDEX idx_executions_tenant_time ON scripting.executions(tenant_id, started_at DESC);

CREATE TABLE scripting.script_chains (
    chain_id        UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    description     TEXT,
    on_failure      TEXT NOT NULL DEFAULT 'stop' CHECK (on_failure IN ('stop', 'continue', 'branch')),
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, name)
);

CREATE TABLE scripting.script_chain_links (
    chain_id        UUID NOT NULL REFERENCES scripting.script_chains(chain_id) ON DELETE CASCADE,
    step_order      INTEGER NOT NULL,
    script_id       UUID NOT NULL REFERENCES scripting.scripts(script_id),
    PRIMARY KEY (chain_id, step_order)
);

-- ============================================================================
-- Notifications (plumbing only — UI lands in Phase 4)
-- ============================================================================
CREATE TABLE notifications.templates (
    template_id     UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    channel         TEXT NOT NULL CHECK (channel IN ('email', 'teams', 'sms', 'webhook')),
    subject_template TEXT,
    body_template   TEXT NOT NULL,
    is_html         BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, name)
);

CREATE TABLE notifications.queue (
    notification_id BIGSERIAL PRIMARY KEY,
    tenant_id       UUID NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    template_id     UUID REFERENCES notifications.templates(template_id),
    channel         TEXT NOT NULL CHECK (channel IN ('email', 'teams', 'sms', 'webhook')),
    recipient       TEXT NOT NULL,
    subject         TEXT,
    body            TEXT NOT NULL,
    is_html         BOOLEAN NOT NULL DEFAULT FALSE,
    priority        SMALLINT NOT NULL DEFAULT 5 CHECK (priority BETWEEN 1 AND 10),
    status          TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sending', 'sent', 'failed', 'cancelled')),
    retry_count     INTEGER NOT NULL DEFAULT 0,
    max_retries     INTEGER NOT NULL DEFAULT 3,
    scheduled_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at         TIMESTAMPTZ,
    failed_at       TIMESTAMPTZ,
    last_error      TEXT,
    source_type     TEXT,
    source_id       TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_queue_pending ON notifications.queue(status, scheduled_at) WHERE status = 'pending';
CREATE INDEX idx_queue_tenant ON notifications.queue(tenant_id, created_at DESC);

-- ============================================================================
-- Row-Level Security
-- ============================================================================
-- Helper function: read current tenant from session GUC
CREATE OR REPLACE FUNCTION core.current_tenant() RETURNS UUID
    LANGUAGE SQL STABLE
    AS $$ SELECT NULLIF(current_setting('app.tenant_id', TRUE), '')::UUID $$;

-- Apply RLS to every table that has a tenant_id
DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN
        SELECT n.nspname, c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND a.attnum > 0
        WHERE c.relkind = 'r'
          AND n.nspname IN ('tenancy', 'identity', 'audit', 'scripting', 'notifications')
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', r.nspname, r.relname);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', r.nspname, r.relname);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON %I.%I USING (tenant_id = core.current_tenant()) WITH CHECK (tenant_id = core.current_tenant())',
            r.nspname, r.relname);
        EXECUTE format(
            'CREATE POLICY system_bypass ON %I.%I AS PERMISSIVE FOR ALL TO PUBLIC USING (core.current_tenant() IS NULL)',
            r.nspname, r.relname);
    END LOOP;
END $$;

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0001_foundations', 'manual')
ON CONFLICT (version) DO NOTHING;
