-- Radiology Plus v2.1 — Patient correction (P1.8)
-- Adds patient-action carrying fields to tech_validation.validations so the wizard
-- can record either an in-place demographic correction (pacs.patients) or a study
-- reassignment to a different existing patient (pacs.studies.patient). These are
-- consumed by DoTheDoOrchestrator at finalize and written through INovaradWriter
-- (dual-audit). Additive + nullable; safe to apply on a live app DB.

ALTER TABLE tech_validation.validations
    ADD COLUMN IF NOT EXISTS patient_action            SMALLINT NOT NULL DEFAULT 0
        CHECK (patient_action BETWEEN 0 AND 2),          -- 0=none, 1=edit-in-place, 2=reassign
    ADD COLUMN IF NOT EXISTS corrected_last_name        TEXT,
    ADD COLUMN IF NOT EXISTS corrected_first_name       TEXT,
    ADD COLUMN IF NOT EXISTS corrected_middle_name      TEXT,
    ADD COLUMN IF NOT EXISTS corrected_birth_date       DATE,
    ADD COLUMN IF NOT EXISTS corrected_gender           TEXT,
    ADD COLUMN IF NOT EXISTS reassign_target_patient_id BIGINT;

COMMENT ON COLUMN tech_validation.validations.patient_action IS
    '0=none, 1=edit demographics in place (UPDATE pacs.patients), 2=reassign study to a different existing patient (UPDATE pacs.studies.patient).';

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0005_patient_correction', 'manual')
ON CONFLICT (version) DO NOTHING;
