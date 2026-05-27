-- Radiology Plus v2.1 — Tech Validation
-- Phase 1. Adds:
--   tech_validation.ready_studies        — snapshot/projection of pacs.studies rows that
--                                          a tech still needs to validate. Populated by
--                                          RadiologyPlus.Service's ReadyStudiesProjector.
--   tech_validation.validations          — per-study wizard state machine + reason notes.
--   tech_validation.tech_notes_templates — canned Reason/Notes presets per tenant.
--   tech_validation.do_the_do_runs       — audit of every "Do the Do" attempt (success or fail).
--
-- RLS on tenant_id is enforced consistent with the foundation migration.

-- ============================================================================
-- Schema
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS tech_validation;
COMMENT ON SCHEMA tech_validation IS 'Tech Validation worklist + 5-step wizard + Do-the-Do audit.';

-- ============================================================================
-- Reference enum-like check-constraint values
-- ============================================================================
-- wizard_step: 1=Study, 2=Order, 3=Reason, 4=Review, 5=Done
-- validation_status: 1=Open, 2=InProgress, 3=Submitted (Do the Do running), 4=Completed, 5=Failed, 6=Cancelled

-- ============================================================================
-- Tables
-- ============================================================================

CREATE TABLE tech_validation.ready_studies (
    -- Composite identity scoped to the tenant + the Novarad study row.
    tenant_id           UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    novarad_study_id    BIGINT      NOT NULL,              -- pacs.studies.id on the tenant's Novarad
    -- Snapshot fields (populated by the projector, kept fresh on each pass)
    facility_id         INTEGER     NOT NULL,              -- pacs.studies.facility_id
    study_uid           TEXT        NOT NULL,
    accession           TEXT,
    study_date          TIMESTAMPTZ,
    modality            TEXT,
    custom_3            TEXT,                              -- the tag the projector filters on
    novarad_patient_id  BIGINT      NOT NULL,              -- pacs.studies.patient → pacs.patients.id
    patient_pid         TEXT,                              -- pacs.patients.patient_id (display)
    patient_last_name   TEXT,
    patient_first_name  TEXT,
    patient_birth_date  DATE,
    last_image_processed_date TIMESTAMPTZ,                 -- the field that gates "ready"
    -- Bookkeeping
    projected_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, novarad_study_id)
);
CREATE INDEX idx_ready_studies_facility ON tech_validation.ready_studies(tenant_id, facility_id);
CREATE INDEX idx_ready_studies_accession ON tech_validation.ready_studies(tenant_id, accession);
CREATE INDEX idx_ready_studies_patient ON tech_validation.ready_studies(tenant_id, novarad_patient_id);
COMMENT ON TABLE tech_validation.ready_studies IS
    'Per-tenant projection of pacs.studies rows that need tech validation. Refreshed by ReadyStudiesProjector on a polling cadence (see Service config).';

CREATE TABLE tech_validation.validations (
    validation_id       UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_id           UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    novarad_study_id    BIGINT      NOT NULL,              -- the study the tech is validating
    novarad_order_id    BIGINT,                            -- selected order (ris.orders.order_id), nullable until step 2
    novarad_patient_id  BIGINT,                            -- the patient that gets corrected during Do-the-Do
    -- Wizard state machine
    current_step        SMALLINT    NOT NULL DEFAULT 1 CHECK (current_step BETWEEN 1 AND 5),
    status              SMALLINT    NOT NULL DEFAULT 1 CHECK (status BETWEEN 1 AND 6),
    -- Selections / notes
    reason              TEXT,
    tech_notes          TEXT,
    referring_physician_id BIGINT,                         -- optional, when step 3.5 is exercised
    comparison_study_ids BIGINT[],                          -- pacs.studies.id values to merge as comparisons
    create_order_requested BOOLEAN NOT NULL DEFAULT FALSE,  -- "Can't find order — create order?" branch
    -- Auditing
    started_by_user_id  UUID        NOT NULL REFERENCES identity.users(user_id),
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    -- Free-form metadata bucket (UI may stash transient values mid-wizard)
    metadata            JSONB       NOT NULL DEFAULT '{}'::jsonb,
    -- Prevent two open wizards on the same study within a tenant.
    CONSTRAINT uq_open_validation_per_study EXCLUDE USING btree
        (tenant_id WITH =, novarad_study_id WITH =) WHERE (status IN (1, 2))
);
CREATE INDEX idx_validations_tenant_status ON tech_validation.validations(tenant_id, status);
CREATE INDEX idx_validations_started_by ON tech_validation.validations(tenant_id, started_by_user_id);
COMMENT ON TABLE tech_validation.validations IS
    'One row per tech-validation wizard run. Open wizards (status 1/2) are mutually exclusive per study + tenant.';

CREATE TABLE tech_validation.tech_notes_templates (
    template_id         BIGSERIAL   PRIMARY KEY,
    tenant_id           UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    label               TEXT        NOT NULL,
    body                TEXT        NOT NULL,
    sort_order          INTEGER     NOT NULL DEFAULT 0,
    is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
    created_by_user_id  UUID        REFERENCES identity.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, label)
);
COMMENT ON TABLE tech_validation.tech_notes_templates IS 'Canned tech-notes snippets shown on wizard step 3.';

CREATE TABLE tech_validation.do_the_do_runs (
    run_id              BIGSERIAL   PRIMARY KEY,
    tenant_id           UUID        NOT NULL REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    validation_id       UUID        NOT NULL REFERENCES tech_validation.validations(validation_id) ON DELETE CASCADE,
    step_key            TEXT        NOT NULL,                              -- e.g. 'pacs.correct_patient', 'ris.put_study_uid'
    step_order          INTEGER     NOT NULL,
    status              SMALLINT    NOT NULL CHECK (status BETWEEN 1 AND 3), -- 1=Started, 2=Succeeded, 3=Failed
    novarad_audit_id    UUID,                                              -- ID returned by INovaradWriter.ExecuteAsync, if any
    error_message       TEXT,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ
);
CREATE INDEX idx_do_the_do_runs_validation ON tech_validation.do_the_do_runs(validation_id, step_order);
COMMENT ON TABLE tech_validation.do_the_do_runs IS
    'Per-step audit for every Do-the-Do attempt. Survives whether the validation succeeded or rolled back.';

-- ============================================================================
-- RLS
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
        WHERE c.relkind = 'r' AND n.nspname = 'tech_validation'
    LOOP
        EXECUTE format('ALTER TABLE tech_validation.%I ENABLE ROW LEVEL SECURITY', r.relname);
        EXECUTE format('ALTER TABLE tech_validation.%I FORCE  ROW LEVEL SECURITY', r.relname);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON tech_validation.%I USING (tenant_id = core.current_tenant()) WITH CHECK (tenant_id = core.current_tenant())',
            r.relname);
        EXECUTE format(
            'CREATE POLICY system_bypass ON tech_validation.%I AS PERMISSIVE FOR ALL TO PUBLIC USING (core.current_tenant() IS NULL)',
            r.relname);
    END LOOP;
END $$;

-- ============================================================================
-- Helper: ready-studies view that joins onto in-flight validation status
-- so the worklist can show "Already in progress by Jane" badges.
-- ============================================================================
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
    v.validation_id   AS in_progress_validation_id,
    v.status          AS in_progress_status,
    v.started_by_user_id AS in_progress_started_by,
    v.started_at      AS in_progress_started_at
FROM tech_validation.ready_studies rs
LEFT JOIN tech_validation.validations v
       ON v.tenant_id        = rs.tenant_id
      AND v.novarad_study_id = rs.novarad_study_id
      AND v.status IN (1, 2);  -- only show open / in-progress
COMMENT ON VIEW tech_validation.vw_worklist IS
    'Worklist projection. One row per ready study with an optional join onto an open validation.';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0002_tech_validation', 'manual')
ON CONFLICT (version) DO NOTHING;
