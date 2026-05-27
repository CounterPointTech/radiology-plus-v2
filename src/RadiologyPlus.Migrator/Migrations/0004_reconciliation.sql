-- Radiology Plus v2.1 — Billing Reconciliation (Phase 2.4)
-- Adds:
--   billing.reconciliation_runs        — header per report run (who, when, window, totals).
--   billing.reconciliation_line_items  — per (radiologist x facility x CPT) line.
--
-- The detail rows are materialized snapshots taken at run time — so a billing
-- run done last quarter still shows the same numbers next quarter, even if
-- billing.cpt_codes.work_rvu has been edited in the meantime.
--
-- Phase 2.4 only writes "preview" rows for now (radiologist + signed-report
-- count, no CPT join). The schema accommodates the full per-CPT shape so we
-- don't have to migrate again when bundles + overrides are resolved.

CREATE SCHEMA IF NOT EXISTS billing;  -- idempotent; created in 0003

-- ============================================================================
-- Tables
-- ============================================================================

CREATE TABLE billing.reconciliation_runs (
    run_id              BIGSERIAL    PRIMARY KEY,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    -- Reporting window, inclusive-from / exclusive-to, in UTC.
    period_start        TIMESTAMPTZ  NOT NULL,
    period_end          TIMESTAMPTZ  NOT NULL CHECK (period_end > period_start),
    -- Optional scoping: NULL = every facility the tenant owns.
    facility_id         BIGINT       REFERENCES tenancy.facilities(facility_id) ON DELETE SET NULL,
    -- 1 = Preview (counts only, no per-CPT reconciliation)
    -- 2 = Final   (per-CPT line items + work-RVU sums)
    run_kind            SMALLINT     NOT NULL CHECK (run_kind IN (1, 2)),
    -- Totals materialized at run time. Mostly informational; the source-of-truth
    -- is reconciliation_line_items.
    total_reports       INTEGER      NOT NULL DEFAULT 0,
    total_radiologists  INTEGER      NOT NULL DEFAULT 0,
    total_work_rvu      NUMERIC(12,4) NOT NULL DEFAULT 0,
    -- Any non-fatal issues we want the reviewer to see (e.g. CPTs missing from
    -- the master, status values we skipped, mismatched RVUs).
    notes               JSONB        NOT NULL DEFAULT '[]'::jsonb,
    generated_by_user_id UUID        NOT NULL REFERENCES identity.users(user_id),
    generated_at        TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_reconciliation_runs_tenant_period
    ON billing.reconciliation_runs(tenant_id, period_start DESC);
COMMENT ON TABLE billing.reconciliation_runs IS
    'Header per reconciliation report. Preview runs (run_kind=1) capture report counts only; final runs (run_kind=2) capture per-CPT line items.';

CREATE TABLE billing.reconciliation_line_items (
    line_id             BIGSERIAL    PRIMARY KEY,
    run_id              BIGINT       NOT NULL REFERENCES billing.reconciliation_runs(run_id) ON DELETE CASCADE,
    tenant_id           UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    -- Who. We snapshot the Novarad physician_id PLUS the readable name at the
    -- time of the run, so a renamed/retired physician keeps a stable label on
    -- old reports.
    novarad_physician_id BIGINT      NOT NULL,
    physician_display_name TEXT      NOT NULL,
    -- Where. Same approach — site_code as Novarad sees it + facility_id as we do.
    site_code           TEXT         NOT NULL,
    facility_id         BIGINT       REFERENCES tenancy.facilities(facility_id) ON DELETE SET NULL,
    -- What.
    cpt_code            TEXT,                                                  -- NULL on preview rows
    cpt_description     TEXT,
    -- How many.
    report_count        INTEGER      NOT NULL DEFAULT 0 CHECK (report_count >= 0),
    units               NUMERIC(10,2) NOT NULL DEFAULT 0 CHECK (units >= 0),
    -- RVU snapshots — captured at run time so historic reports are stable.
    work_rvu_per_unit   NUMERIC(8,4) NOT NULL DEFAULT 0,                       -- from billing.cpt_codes at run time
    work_rvu_total      NUMERIC(12,4) NOT NULL DEFAULT 0,                      -- = units * work_rvu_per_unit
    novarad_rvu_work    NUMERIC(8,4),                                          -- billing_service_codes.rvu_work at run time, for mismatch detection
    -- Has-mismatch flag computed for quick filtering. Set when
    -- (novarad_rvu_work IS NOT NULL AND novarad_rvu_work <> work_rvu_per_unit).
    rvu_mismatch        BOOLEAN      NOT NULL DEFAULT FALSE,
    -- For traceability back to Novarad. Stored as a JSONB array so a single
    -- line item can list the report_ids that contributed.
    novarad_report_ids  BIGINT[]     NOT NULL DEFAULT '{}'::bigint[]
);
CREATE INDEX idx_reconciliation_lines_run
    ON billing.reconciliation_line_items(run_id);
CREATE INDEX idx_reconciliation_lines_physician
    ON billing.reconciliation_line_items(tenant_id, novarad_physician_id);
CREATE INDEX idx_reconciliation_lines_mismatch
    ON billing.reconciliation_line_items(run_id) WHERE rvu_mismatch = TRUE;
COMMENT ON TABLE billing.reconciliation_line_items IS
    'Per-(radiologist, facility, CPT) line items for a reconciliation run. Snapshotted at run time so historic numbers don''t drift when the CPT master gets edited later.';

-- ============================================================================
-- RLS — same pattern as billing.cpt_codes
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
          AND c.relname IN ('reconciliation_runs', 'reconciliation_line_items')
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
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0004_reconciliation', 'manual')
ON CONFLICT (version) DO NOTHING;
