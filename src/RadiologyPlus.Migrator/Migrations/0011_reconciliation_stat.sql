-- Radiology Plus v2.1 — Reconciliation STAT subtotals (client feedback round 3, item 1.3)
-- Adds:
--   billing.reconciliation_runs.stat_report_count            — run-level count of distinct
--                                                              credited reports flagged STAT.
--   billing.reconciliation_line_items.novarad_stat_report_ids — the STAT subset of
--                                                              novarad_report_ids, so per-facility
--                                                              STAT subtotals rebuild on export
--                                                              without a separate summary table.
--
-- Source of truth for STAT is Novarad's ris.order_procedures.stat_flag, read at run time.
-- Both columns are additive run-time snapshots — historic runs keep their numbers.

ALTER TABLE billing.reconciliation_runs
    ADD COLUMN IF NOT EXISTS stat_report_count INTEGER NOT NULL DEFAULT 0;

ALTER TABLE billing.reconciliation_line_items
    ADD COLUMN IF NOT EXISTS novarad_stat_report_ids BIGINT[] NOT NULL DEFAULT '{}'::bigint[];

COMMENT ON COLUMN billing.reconciliation_runs.stat_report_count IS
    'Distinct credited reports flagged STAT (ris.order_procedures.stat_flag) at run time.';
COMMENT ON COLUMN billing.reconciliation_line_items.novarad_stat_report_ids IS
    'The STAT subset of novarad_report_ids for this line; enables per-facility STAT subtotals on re-materialization.';

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0011_reconciliation_stat', 'manual')
ON CONFLICT (version) DO NOTHING;
