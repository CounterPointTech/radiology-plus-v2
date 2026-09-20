-- Radiology Plus v2.1 — completed studies drop off the tech worklist
--
-- Blocker (2026-08-06): a study stayed on the Tech Validation worklist after it was
-- validated (Finalize), and a losing study stayed on after a duplicate-study merge.
-- Those are one bug, not two.
--
-- NovaradStudyReader.ReadReadyStudiesAsync selects purely on
--     pacs.studies.status = 0  AND  last_image_processed_date within the lookback window
-- and NEITHER Finalize nor the merge ever writes pacs.studies.status. A completed study
-- therefore still satisfies the source query, so the 60s projector pass keeps re-inserting
-- it. That is why deleting the ready_studies row cannot fix this on its own — the next
-- pass puts it straight back. Note also that the merge's only removal step is
-- is_valid = FALSE, which the reader deliberately ignores (is_valid = FALSE is the normal
-- majority state on the customer clone, not a deletion marker).
--
-- We record completion on OUR side rather than writing status back into the customer's
-- Novarad (Dan's call, 2026-08-06). completed_studies is a suppression list: the worklist
-- view excludes anything listed, and the projector's upsert skips it, so a completed study
-- neither appears on the worklist nor churns in the projection every minute.
--
-- reason separates the two paths so support can tell a merge from a validation.
-- validation_id is the revert handle: it cascades, so deleting a validations row (as the
-- rehearsal reverts in .claude/state/session-9-revert.md do) automatically releases the
-- suppression and the study returns to the worklist on the next projector pass.
--
-- Fully reversible with no Novarad write: DELETE the row and the study comes back.

-- ============================================================================
-- Table
-- ============================================================================

CREATE TABLE tech_validation.completed_studies (
    tenant_id        UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    novarad_study_id BIGINT      NOT NULL,              -- pacs.studies.id on the tenant's Novarad
    reason           TEXT        NOT NULL,
    -- Set for reason='validated'. ON DELETE CASCADE so reverting a validation also
    -- un-suppresses the study. NULL for reason='merged_loser' (a merge has no validation).
    validation_id    UUID        REFERENCES tech_validation.validations(validation_id) ON DELETE CASCADE,
    completed_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, novarad_study_id),
    CONSTRAINT ck_completed_studies_reason CHECK (reason IN ('validated', 'merged_loser'))
);

-- Supports the ON DELETE CASCADE from validations without a sequential scan.
CREATE INDEX idx_completed_studies_validation ON tech_validation.completed_studies(validation_id);

COMMENT ON TABLE tech_validation.completed_studies IS
    'Suppression list for the tech worklist: studies whose work is done (validated, or merged away as a duplicate). The source query in NovaradStudyReader cannot tell — it only sees pacs.studies.status, which we never write — so completion is tracked here instead. Delete a row to put the study back on the worklist.';
COMMENT ON COLUMN tech_validation.completed_studies.reason IS
    '''validated'' = finished through Do-the-Do; ''merged_loser'' = merged into a keeper study and no longer independently workable.';

-- ============================================================================
-- Backfill — studies already validated before this migration
-- ============================================================================
-- Runs BEFORE row-level security is enabled below, so it is not subject to the
-- tenant predicate. DISTINCT ON collapses studies validated more than once
-- (re-runs after a revert) to their most recent completed validation.
--
-- Merge losers are deliberately NOT backfilled: we have no reliable record of past
-- merge losers outside the Novarad audit trail, and suppressing a study on a guess
-- would silently remove work from a tech's queue.

INSERT INTO tech_validation.completed_studies (tenant_id, novarad_study_id, reason, validation_id, completed_at)
SELECT DISTINCT ON (v.tenant_id, v.novarad_study_id)
       v.tenant_id, v.novarad_study_id, 'validated', v.validation_id, COALESCE(v.completed_at, NOW())
FROM tech_validation.validations v
WHERE v.status = 4                                  -- 4 = Completed (see 0002 header)
ORDER BY v.tenant_id, v.novarad_study_id, v.completed_at DESC NULLS LAST
ON CONFLICT (tenant_id, novarad_study_id) DO NOTHING;

-- ============================================================================
-- Row-level security — mirrors the tech_validation policies created in 0002
-- ============================================================================

ALTER TABLE tech_validation.completed_studies ENABLE ROW LEVEL SECURITY;
ALTER TABLE tech_validation.completed_studies FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON tech_validation.completed_studies
    USING (tenant_id = core.current_tenant())
    WITH CHECK (tenant_id = core.current_tenant());

CREATE POLICY system_bypass ON tech_validation.completed_studies
    AS PERMISSIVE FOR ALL TO PUBLIC
    USING (core.current_tenant() IS NULL);

-- ============================================================================
-- Worklist view — exclude completed studies
-- ============================================================================
-- Unchanged from 0009 apart from the NOT EXISTS. Kept as a full CREATE OR REPLACE
-- because the column list must stay byte-identical for REPLACE to be legal.

CREATE OR REPLACE VIEW tech_validation.vw_worklist AS
SELECT
    rs.tenant_id,
    rs.novarad_study_id,
    rs.facility_id,
    rs.study_uid,
    rs.accession,
    rs.study_date,
    rs.modality,
    rs.custom_3,
    rs.novarad_patient_id,
    rs.patient_pid,
    rs.patient_last_name,
    rs.patient_first_name,
    rs.patient_birth_date,
    rs.last_image_processed_date,
    rs.projected_at,
    v.validation_id      AS in_progress_validation_id,
    v.status             AS in_progress_status,
    v.started_by_user_id AS in_progress_started_by,
    v.started_at         AS in_progress_started_at,
    rs.patient_gender,
    rs.study_description
FROM tech_validation.ready_studies rs
LEFT JOIN tech_validation.validations v
       ON v.tenant_id        = rs.tenant_id
      AND v.novarad_study_id = rs.novarad_study_id
      AND v.status IN (1, 2)
WHERE NOT EXISTS (
    SELECT 1
    FROM tech_validation.completed_studies c
    WHERE c.tenant_id        = rs.tenant_id
      AND c.novarad_study_id = rs.novarad_study_id
);

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0023_completed_studies', 'manual')
ON CONFLICT (version) DO NOTHING;
