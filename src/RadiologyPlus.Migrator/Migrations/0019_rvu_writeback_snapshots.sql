-- Radiology Plus v2.1 — M*Modal RVU write-back backups (restore points)
--
-- Before every Apply (and on demand), snapshot the target facility's current
-- [Exam].[ExamCode].[RelativeValueUnit] values into OUR Postgres, so a bad sync can be
-- reverted and testing is safe (snapshot -> experiment -> restore). Snapshots live here,
-- independent of M*Modal, so they survive even if the dictation DB gets mangled.
--
--   billing.rvu_writeback_snapshots      — one header per backup.
--       issuer_key NULL = whole-DB (all issuers); set = one facility.
--       source: 'auto_pre_apply' (captured before an Apply) | 'manual' (Back up now) |
--               'import' (loaded from a CSV).
--   billing.rvu_writeback_snapshot_rows  — the captured (issuer, code) -> RVU values.
--       relative_value_unit is NULLABLE so a snapshot faithfully captures blanks; a
--       restore writes them back verbatim.
--
-- RLS on tenant_id, consistent with the rest of billing.

CREATE TABLE billing.rvu_writeback_snapshots (
    snapshot_id        BIGSERIAL   PRIMARY KEY,
    tenant_id          UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    issuer_key         UUID,                                  -- NULL = all issuers
    label              TEXT        NOT NULL,
    source             TEXT        NOT NULL CHECK (source IN ('auto_pre_apply', 'manual', 'import')),
    row_count          INTEGER     NOT NULL DEFAULT 0,
    created_by_user_id UUID        NOT NULL REFERENCES identity.users(user_id),
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_rvu_wb_snapshots_tenant ON billing.rvu_writeback_snapshots(tenant_id, created_at DESC);
COMMENT ON TABLE billing.rvu_writeback_snapshots IS
    'Header per M*Modal RVU backup. issuer_key NULL = all issuers. source: auto_pre_apply | manual | import.';

CREATE TABLE billing.rvu_writeback_snapshot_rows (
    snapshot_id         BIGINT NOT NULL REFERENCES billing.rvu_writeback_snapshots(snapshot_id) ON DELETE CASCADE,
    tenant_id           UUID   NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    issuer_key          UUID   NOT NULL,
    code                TEXT   NOT NULL,
    relative_value_unit DOUBLE PRECISION,                     -- nullable: captures/restores blanks verbatim
    PRIMARY KEY (snapshot_id, issuer_key, code)
);
COMMENT ON TABLE billing.rvu_writeback_snapshot_rows IS
    'Captured [Exam].[ExamCode] RVU values for a snapshot. relative_value_unit NULL = the M*Modal value was blank.';

-- ============================================================================
-- RLS — same tenant isolation as the rest of billing.
-- ============================================================================
DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY['rvu_writeback_snapshots', 'rvu_writeback_snapshot_rows']
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
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0019_rvu_writeback_snapshots', 'manual')
ON CONFLICT (version) DO NOTHING;
