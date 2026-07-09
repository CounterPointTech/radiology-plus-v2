-- Radiology Plus v2.1 — record the issuer scope of each M*Modal write-back run
--
-- The M*Modal ClinicalDataStore is shared by many issuers (facilities); the RVU
-- write-back targets ONE issuer per run (the common case) or, deliberately, ALL of
-- them (a warned power option). Record which, so the run history shows the scope
-- ("synced Salient: 42 updated" vs "synced ALL facilities").
--
--   issuer_key NULL  = the run targeted ALL issuers
--   issuer_key set   = the run targeted that single Clinical.Issuer (M*Modal-side GUID)
--
-- The issuer's display name is resolved client-side from the live issuer list, so no
-- label is stored here. Purely additive; existing rows keep NULL (they predate
-- per-issuer scoping and were tenant-wide-effective pushes).

ALTER TABLE billing.rvu_sync_runs
    ADD COLUMN issuer_key UUID;

COMMENT ON COLUMN billing.rvu_sync_runs.issuer_key IS
    'M*Modal Clinical.Issuer this run targeted. NULL = all issuers (the warned power option); non-NULL = a single facility.';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0018_rvu_sync_issuer', 'manual')
ON CONFLICT (version) DO NOTHING;
