-- Radiology Plus v2.1 — M*Modal RVU write-back (project-ffi-rvu-writeback)
--
-- Lets the billing module push our effective CMS/curated work RVUs OUT to the
-- customer's M*Modal "ClinicalDataStore" dictation DB (SQL Server), so its stored
-- RVUs (`[Exam].[ExamCode].[RelativeValueUnit]`) match what reconciliation credits.
-- Mirrors the live-write conventions already used for Novarad: a per-tenant,
-- encrypted connection row + an append-only run-history table; the write itself is
-- transactional and dual-audited into audit.access_logs (action 10 = MModalWrite).
--
-- Adds:
--   tenancy.mmodal_connections — per-tenant SQL Server connection to ClinicalDataStore.
--                                Mirrors tenancy.novarad_connections (password is
--                                symmetrically encrypted with the app master key). NULL
--                                rows / absent tenant = NOT configured -> the sink is a
--                                no-op (nothing is ever written to a live DB until a
--                                connection is configured). Optional issuer_key scopes
--                                writes to one Clinical.Issuer namespace; NULL = update
--                                the active row for a code across all issuers.
--   billing.rvu_sync_runs      — audit header per sync run (mirror billing.rvu_imports):
--                                who ran it, the year/quarter snapshot, whether it was a
--                                dry-run (preview) or a real apply, and the matched /
--                                updated / unchanged / missing counts.
--
-- RLS: billing.rvu_sync_runs follows the same tenant isolation as the rest of billing.
-- tenancy.mmodal_connections has no RLS (read unscoped with an explicit tenant filter,
-- exactly like tenancy.novarad_connections).

-- ============================================================================
-- Per-tenant M*Modal connection (SQL Server)
-- ============================================================================
CREATE TABLE tenancy.mmodal_connections (
    tenant_id          UUID PRIMARY KEY REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    host               TEXT    NOT NULL,
    port               INTEGER NOT NULL DEFAULT 1433,
    database_name      TEXT    NOT NULL DEFAULT 'ClinicalDataStore',
    username           TEXT    NOT NULL,
    password_encrypted BYTEA   NOT NULL,
    use_ssl            BOOLEAN NOT NULL DEFAULT TRUE,   -- Encrypt=True on the SQL Server connection
    trust_server_cert  BOOLEAN NOT NULL DEFAULT TRUE,   -- self-signed certs are common on-prem
    issuer_key         UUID,                            -- optional Clinical.Issuer scope; NULL = all issuers
    notes              TEXT,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE tenancy.mmodal_connections IS
    'Per-tenant connection to the M*Modal ClinicalDataStore (SQL Server) for the RVU write-back. Password is symmetrically encrypted with the app master key. Absent row = write-back not configured (sink is a no-op).';
COMMENT ON COLUMN tenancy.mmodal_connections.issuer_key IS
    'Optional Clinical.Issuer scope. NULL updates the active [Exam].[ExamCode] row for a code across all issuers; set restricts the UPDATE to one issuer namespace.';

-- ============================================================================
-- Sync run history (mirror billing.rvu_imports)
-- ============================================================================
CREATE TABLE billing.rvu_sync_runs (
    sync_run_id     BIGSERIAL    PRIMARY KEY,
    tenant_id       UUID         NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    year            SMALLINT     NOT NULL,
    quarter         CHAR(1)      NOT NULL CHECK (quarter IN ('A','B','C','D')),
    dry_run         BOOLEAN      NOT NULL DEFAULT FALSE,   -- TRUE = preview only, no rows written
    matched_rows    INTEGER      NOT NULL DEFAULT 0,       -- effective codes that exist in M*Modal
    updated_rows    INTEGER      NOT NULL DEFAULT 0,       -- rows whose RVU actually changed
    unchanged_rows  INTEGER      NOT NULL DEFAULT 0,       -- already equal, skipped
    missing_rows    INTEGER      NOT NULL DEFAULT 0,       -- effective codes with no active M*Modal row
    success         BOOLEAN      NOT NULL DEFAULT TRUE,
    error_message   TEXT,
    ran_by_user_id  UUID         NOT NULL REFERENCES identity.users(user_id),
    ran_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_rvu_sync_runs_tenant ON billing.rvu_sync_runs(tenant_id, year, quarter, ran_at DESC);
COMMENT ON TABLE billing.rvu_sync_runs IS
    'Audit header per M*Modal RVU write-back run. dry_run=TRUE rows are previews (no write). Surfaces who ran it, the year/quarter, and matched/updated/unchanged/missing counts.';

-- ============================================================================
-- RLS — same tenant isolation as the rest of billing (scoped to the new table).
-- ============================================================================
ALTER TABLE billing.rvu_sync_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.rvu_sync_runs FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.rvu_sync_runs
    USING (tenant_id = core.current_tenant())
    WITH CHECK (tenant_id = core.current_tenant());
CREATE POLICY system_bypass ON billing.rvu_sync_runs
    AS PERMISSIVE FOR ALL TO PUBLIC
    USING (core.current_tenant() IS NULL);

-- Document the new audit action (10 = MModalWrite); the column itself needs no change.
COMMENT ON TABLE audit.access_logs IS 'Append-only application audit. action: 1=Login 2=Logout 3=Read 4=Create 5=Update 6=Delete 7=Execute 8=NovaradWrite 9=PermissionDenied 10=MModalWrite.';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0017_mmodal_writeback', 'manual')
ON CONFLICT (version) DO NOTHING;
