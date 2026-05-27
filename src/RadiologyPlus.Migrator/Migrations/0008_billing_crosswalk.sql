-- Radiology Plus v2.1 — Billing Phase 2: service_code → CPT crosswalk
--
-- Lets a billing-authorized user (NRS/Admin) map Novarad's local service codes
-- (e.g. XRCXR2, BEDCXR, 3206048) to real CPT codes in our master. The matcher
-- consults approved mappings BEFORE the bundle/singleton lookup, so mapped
-- codes start crediting RVUs and drop off the Unmapped Codes report.
--
-- Year-agnostic: a mnemonic resolves to one CPT regardless of year; the
-- year-keyed RVU still comes from billing.cpt_codes.
--
-- No writes to Novarad — the crosswalk lives entirely in rad_plus.

-- ============================================================================
-- Tables
-- ============================================================================

CREATE TABLE billing.service_code_crosswalk (
    crosswalk_id             BIGSERIAL    PRIMARY KEY,
    tenant_id                UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    service_code             TEXT         NOT NULL,       -- raw Novarad code, normalized to TRIM/UPPER
    cpt_code                 TEXT         NOT NULL,       -- target; matcher resolves against billing.cpt_codes
    -- 1 = approved (credit through this mapping)
    -- 2 = suppressed (never credit this code; soft-delete tombstone)
    status                   SMALLINT     NOT NULL DEFAULT 1 CHECK (status IN (1, 2)),
    -- 1 = manual entry, 2 = accepted suggestion, 3 = bulk CSV import
    source                   SMALLINT     NOT NULL CHECK (source IN (1, 2, 3)),
    note                     TEXT,                        -- free-form, e.g. "accepted suggestion, score 0.42"
    approved_for_description TEXT,                        -- snapshot of Novarad description at approval (audit aid)
    applied_count            BIGINT       NOT NULL DEFAULT 0,   -- bumped by reconciliation runs (telemetry)
    last_used_at             TIMESTAMPTZ,                       -- updated by reconciliation runs
    created_by_user_id       UUID         NOT NULL REFERENCES identity.users(user_id),
    updated_by_user_id       UUID         REFERENCES identity.users(user_id),
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX uq_crosswalk_tenant_service
    ON billing.service_code_crosswalk (tenant_id, service_code);

-- Hot path: matcher loads (tenant_id, status=1) → service_code/cpt_code dict;
-- management page filters by status. Index supports both.
CREATE INDEX idx_crosswalk_tenant_status
    ON billing.service_code_crosswalk (tenant_id, status);

COMMENT ON TABLE billing.service_code_crosswalk IS
    'Per-tenant service_code → cpt_code crosswalk. The reconciliation matcher consults approved rows (status=1) before the bundle/singleton lookup. Year-agnostic by design.';

-- ============================================================================
-- RLS — same pattern as 0003_billing.sql. Tenant isolation by core.current_tenant().
-- Picks up every billing.* table that has a tenant_id column, so this re-runs the
-- loop and idempotently re-applies policies for the new table.
-- ============================================================================
DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND a.attnum > 0
        WHERE c.relkind = 'r' AND n.nspname = 'billing'
          AND c.relname = 'service_code_crosswalk'
    LOOP
        EXECUTE format('ALTER TABLE billing.%I ENABLE ROW LEVEL SECURITY', r.relname);
        EXECUTE format('ALTER TABLE billing.%I FORCE  ROW LEVEL SECURITY', r.relname);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON billing.%I USING (tenant_id = core.current_tenant()) WITH CHECK (tenant_id = core.current_tenant())',
            r.relname);
        EXECUTE format(
            'CREATE POLICY system_bypass ON billing.%I AS PERMISSIVE FOR ALL TO PUBLIC USING (core.current_tenant() IS NULL)',
            r.relname);
    END LOOP;
END $$;

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0008_billing_crosswalk', 'manual')
ON CONFLICT (version) DO NOTHING;
