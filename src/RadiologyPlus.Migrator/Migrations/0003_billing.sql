-- Radiology Plus v2.1 — Billing Validation (Phase 2)
-- Adds:
--   billing.cpt_codes      — per-tenant, per-year CPT master.
--                            Loaded from the customer's annual RVU spreadsheet
--                            (see RadiologyPlus.Data/Billing/CptMasterImporter).
--   billing.rvu_overrides  — per-tenant, optionally per-facility RVU override
--                            for a given CPT/year. NULL facility_id = tenant-wide.
--   billing.cpt_imports    — audit row per import run (file name + sheet +
--                            who ran it + counts + any per-row errors).
--
-- Reconciliation + dashboards (P2.4 / P2.5) land in a later migration once the
-- Novarad report-side schema is locked.
--
-- RLS on tenant_id is enforced consistent with the foundation migration.

-- ============================================================================
-- Extensions (required before the trigram index on cpt_codes.description)
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ============================================================================
-- Schema
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS billing;
COMMENT ON SCHEMA billing IS 'Billing Validation: CPT master, RVU overrides, reconciliation.';

-- ============================================================================
-- Tables
-- ============================================================================

CREATE TABLE billing.cpt_codes (
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    year                SMALLINT     NOT NULL CHECK (year BETWEEN 2000 AND 2100),
    cpt_code            TEXT         NOT NULL,         -- HCPCS-style; usually 5 chars, sometimes letter-prefixed (g0279, etc.)
    description         TEXT         NOT NULL,
    work_rvu            NUMERIC(8,4) NOT NULL CHECK (work_rvu >= 0),
    -- Optional spec carry-over fields from the spreadsheet that we don't yet model.
    notes               TEXT,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    -- Bookkeeping
    imported_from_import_id BIGINT,                    -- FK set after billing.cpt_imports row exists; nullable for hand-edits
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, year, cpt_code)
);
CREATE INDEX idx_cpt_codes_tenant_year ON billing.cpt_codes(tenant_id, year);
CREATE INDEX idx_cpt_codes_search ON billing.cpt_codes USING gin (description gin_trgm_ops);
COMMENT ON TABLE billing.cpt_codes IS
    'Per-tenant CPT master keyed by (tenant, year, cpt). work_rvu is the billing-reconciliation figure used for radiologist work-RVU credit.';

CREATE TABLE billing.rvu_overrides (
    override_id         BIGSERIAL    PRIMARY KEY,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    year                SMALLINT     NOT NULL CHECK (year BETWEEN 2000 AND 2100),
    cpt_code            TEXT         NOT NULL,
    -- NULL = applies to every facility in the tenant. Specific id = override scoped to that facility.
    facility_id         BIGINT       REFERENCES tenancy.facilities(facility_id) ON DELETE CASCADE,
    override_work_rvu   NUMERIC(8,4) NOT NULL CHECK (override_work_rvu >= 0),
    note                TEXT,
    created_by_user_id  UUID         REFERENCES identity.users(user_id),
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
-- Two unique guards because Postgres can't put NULL inside a composite UNIQUE
-- the way we want (NULL would compare unequal to NULL otherwise).
CREATE UNIQUE INDEX uq_rvu_override_tenant_year_cpt_facility
    ON billing.rvu_overrides (tenant_id, year, cpt_code, facility_id)
    WHERE facility_id IS NOT NULL;
CREATE UNIQUE INDEX uq_rvu_override_tenant_year_cpt_all
    ON billing.rvu_overrides (tenant_id, year, cpt_code)
    WHERE facility_id IS NULL;
COMMENT ON TABLE billing.rvu_overrides IS
    'Per-tenant RVU overrides. NULL facility_id = tenant-wide override; non-NULL = facility-specific.';

CREATE TABLE billing.cpt_imports (
    import_id           BIGSERIAL    PRIMARY KEY,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    file_name           TEXT         NOT NULL,
    sheet_name          TEXT         NOT NULL,
    year                SMALLINT     NOT NULL,
    parsed_rows         INTEGER      NOT NULL DEFAULT 0,
    inserted_rows       INTEGER      NOT NULL DEFAULT 0,
    updated_rows        INTEGER      NOT NULL DEFAULT 0,
    skipped_rows        INTEGER      NOT NULL DEFAULT 0,
    errors              JSONB        NOT NULL DEFAULT '[]'::jsonb,   -- [{row, cpt, message}]
    ran_by_user_id      UUID         NOT NULL REFERENCES identity.users(user_id),
    ran_at              TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_cpt_imports_tenant_year ON billing.cpt_imports(tenant_id, year, ran_at DESC);
COMMENT ON TABLE billing.cpt_imports IS
    'Audit header per CPT-master import run. Surfaces who ran it, what file/sheet, and per-row errors.';

ALTER TABLE billing.cpt_codes
    ADD CONSTRAINT fk_cpt_codes_import
    FOREIGN KEY (imported_from_import_id) REFERENCES billing.cpt_imports(import_id) ON DELETE SET NULL;

-- ============================================================================
-- RLS — same pattern as tech_validation. Tenant isolation by core.current_tenant().
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
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0003_billing', 'manual')
ON CONFLICT (version) DO NOTHING;
