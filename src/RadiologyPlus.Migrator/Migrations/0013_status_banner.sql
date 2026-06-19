-- Radiology Plus v2.1 - Admin status banner (app-wide PACS/RIS-down notice)
-- Adds:
--   core.status_banners - per-tenant, admin-authored status banner shown at the top of
--                         the app to every signed-in user. Authored by NRS/Admin; seen
--                         by all roles. severity is an enum (1=info 2=maintenance
--                         3=warning 4=critical) the UI maps to a color + icon.
--
-- First tenant-scoped table in the core schema, so RLS is enabled explicitly here
-- (mirrors the per-table block in 0012_rvu_values; core.current_tenant() lives in core).
--
-- v1 columns: message, severity, is_animated, is_active, starts_at, ends_at.
-- facility_id and is_dismissible are provisioned now (nullable / default) so per-facility
-- targeting and per-user dismissal can land later with no migration; v1 leaves them at
-- their defaults.

CREATE TABLE core.status_banners (
    banner_id           BIGSERIAL    PRIMARY KEY,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,

    message             TEXT         NOT NULL,
    severity            SMALLINT     NOT NULL DEFAULT 1,     -- 1=info 2=maintenance 3=warning 4=critical
    is_animated         BOOLEAN      NOT NULL DEFAULT TRUE,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,  -- the "enabled"/visible flag

    -- Scheduling (v1): null start = show immediately, null end = no auto-expire.
    starts_at           TIMESTAMPTZ,
    ends_at             TIMESTAMPTZ,

    -- Provisioned for later extras; v1 leaves these at their defaults.
    facility_id         BIGINT,                              -- null = all facilities
    is_dismissible      BOOLEAN      NOT NULL DEFAULT FALSE, -- false = sticky

    created_by_user_id  UUID         REFERENCES identity.users(user_id),
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT chk_status_banners_severity CHECK (severity BETWEEN 1 AND 4),
    CONSTRAINT chk_status_banners_message  CHECK (length(btrim(message)) > 0)
);

-- Serves the active-banner poll (tenant + is_active, optional facility) and tenant-only
-- admin scans via the left prefix. A handful of rows per tenant, so one index suffices.
CREATE INDEX idx_status_banners_tenant_active ON core.status_banners(tenant_id, is_active);

COMMENT ON TABLE core.status_banners IS
    'Per-tenant admin-authored app-wide status banner (e.g. PACS/RIS-down notice).';

-- ============================================================================
-- RLS - first tenant-scoped table in core; enable explicitly (mirrors 0012_rvu_values).
-- ============================================================================
DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY['status_banners']
    LOOP
        EXECUTE format('ALTER TABLE core.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE core.%I FORCE  ROW LEVEL SECURITY', t);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON core.%I USING (tenant_id = core.current_tenant()) WITH CHECK (tenant_id = core.current_tenant())',
            t);
        EXECUTE format(
            'CREATE POLICY system_bypass ON core.%I AS PERMISSIVE FOR ALL TO PUBLIC USING (core.current_tenant() IS NULL)',
            t);
    END LOOP;
END $$;

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0013_status_banner', 'manual')
ON CONFLICT (version) DO NOTHING;
