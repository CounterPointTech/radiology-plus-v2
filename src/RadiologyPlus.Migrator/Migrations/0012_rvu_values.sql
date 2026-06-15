-- Radiology Plus v2.1 — CMS RVU source-of-truth (client feedback round 3, item 1.2)
-- Adds:
--   billing.rvu_values  — per-tenant, per-(year,quarter) snapshot of the CMS PFS
--                         Relative Value File (PPRRVU), full component set. Keyed by
--                         (tenant, year, quarter, hcpcs, modifier) because a HCPCS
--                         carries up to three rows: '' (global), '26' (professional),
--                         'TC' (technical). work_rvu is the figure reconciliation
--                         credits against; the rest are carried for completeness and
--                         the future M*Modal write-back.
--   billing.rvu_imports — audit row per CMS file load (mirror billing.cpt_imports).
--
-- Loaded by RadiologyPlus.Data/Billing/RvuValuesImporter from the CMS RVUyy{A-D} zip
-- (or the bare PPRRVU csv). RVU resolution precedence in reconciliation/unmapped is
-- rvu_overrides -> rvu_values -> cpt_codes (cpt_codes keeps Amber's bundles + fallback).
--
-- RLS on tenant_id, consistent with 0003_billing.

-- ============================================================================
-- Tables
-- ============================================================================

CREATE TABLE billing.rvu_values (
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    year                SMALLINT     NOT NULL CHECK (year BETWEEN 2000 AND 2100),
    quarter             CHAR(1)      NOT NULL CHECK (quarter IN ('A','B','C','D')),  -- A=Jan B=Apr C=Jul D=Oct
    hcpcs               TEXT         NOT NULL,        -- HCPCS/CPT code (5 chars; category-II end in 'F', etc.)
    modifier            TEXT         NOT NULL DEFAULT '',  -- '' global, '26' professional, 'TC' technical
    description         TEXT,                          -- CMS short descriptor (AMA-copyright; carried for reference)
    work_rvu            NUMERIC(8,4) NOT NULL CHECK (work_rvu >= 0),
    pe_rvu_nonfac       NUMERIC(8,4),                  -- practice-expense RVU, non-facility
    pe_rvu_fac          NUMERIC(8,4),                  -- practice-expense RVU, facility
    mp_rvu              NUMERIC(8,4),                  -- malpractice RVU
    total_nonfac        NUMERIC(8,4),                  -- total RVU, non-facility
    total_fac           NUMERIC(8,4),                  -- total RVU, facility
    status_code         TEXT,                          -- CMS status indicator (A=active, I/X/etc.)
    global_days         TEXT,                          -- global surgery period (000/010/090/XXX/YYY/ZZZ)
    effective_from      DATE,                          -- derived from (year, quarter): A=Jan1 B=Apr1 C=Jul1 D=Oct1
    source_import_id    BIGINT,                        -- FK set after billing.rvu_imports row exists; nullable for hand-edits
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, year, quarter, hcpcs, modifier)
);
CREATE INDEX idx_rvu_values_tenant_hcpcs ON billing.rvu_values(tenant_id, hcpcs);
CREATE INDEX idx_rvu_values_search ON billing.rvu_values USING gin (description gin_trgm_ops);
-- Serves the reconciliation/unmapped precedence overlay's per-run query:
--   SELECT DISTINCT ON (year, hcpcs) ... WHERE modifier='' ORDER BY year, hcpcs, quarter DESC.
CREATE INDEX idx_rvu_values_overlay ON billing.rvu_values(tenant_id, year, hcpcs, quarter DESC) WHERE modifier = '';
COMMENT ON TABLE billing.rvu_values IS
    'Per-tenant CMS PFS Relative Value snapshot (PPRRVU). work_rvu is the reconciliation-credit figure; per-HCPCS source of truth, above cpt_codes.';

CREATE TABLE billing.rvu_imports (
    import_id           BIGSERIAL    PRIMARY KEY,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    file_name           TEXT         NOT NULL,
    year                SMALLINT     NOT NULL,
    quarter             CHAR(1)      NOT NULL,
    parsed_rows         INTEGER      NOT NULL DEFAULT 0,
    inserted_rows       INTEGER      NOT NULL DEFAULT 0,
    updated_rows        INTEGER      NOT NULL DEFAULT 0,
    skipped_rows        INTEGER      NOT NULL DEFAULT 0,
    errors              JSONB        NOT NULL DEFAULT '[]'::jsonb,   -- [{row, cpt, message}]
    ran_by_user_id      UUID         NOT NULL REFERENCES identity.users(user_id),
    ran_at              TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_rvu_imports_tenant ON billing.rvu_imports(tenant_id, year, quarter, ran_at DESC);
COMMENT ON TABLE billing.rvu_imports IS
    'Audit header per CMS RVU file import run. Surfaces who ran it, what file, the year/quarter, and per-row errors.';

ALTER TABLE billing.rvu_values
    ADD CONSTRAINT fk_rvu_values_import
    FOREIGN KEY (source_import_id) REFERENCES billing.rvu_imports(import_id) ON DELETE SET NULL;

-- ============================================================================
-- RLS — same tenant isolation as the rest of billing. Scoped to the two NEW
-- tables explicitly (the 0003 loop already created policies on the existing ones).
-- ============================================================================
DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY['rvu_values', 'rvu_imports']
    LOOP
        EXECUTE format('ALTER TABLE billing.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE billing.%I FORCE  ROW LEVEL SECURITY', t);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON billing.%I USING (tenant_id = core.current_tenant()) WITH CHECK (tenant_id = core.current_tenant())',
            t);
        EXECUTE format(
            'CREATE POLICY system_bypass ON billing.%I AS PERMISSIVE FOR ALL TO PUBLIC USING (core.current_tenant() IS NULL)',
            t);
    END LOOP;
END $$;

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0012_rvu_values', 'manual')
ON CONFLICT (version) DO NOTHING;
