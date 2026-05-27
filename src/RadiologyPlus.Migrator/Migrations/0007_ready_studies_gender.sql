-- Radiology Plus v2.1 — carry patient gender on the worklist projection
-- So the Tech Validation wizard's Study step can show the patient's sex. Additive;
-- the projector backfills it on the next pass. patient_gender appended to vw_worklist
-- at the end so CREATE OR REPLACE keeps the existing leading columns intact.

ALTER TABLE tech_validation.ready_studies ADD COLUMN IF NOT EXISTS patient_gender TEXT;

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
    rs.patient_gender
FROM tech_validation.ready_studies rs
LEFT JOIN tech_validation.validations v
       ON v.tenant_id        = rs.tenant_id
      AND v.novarad_study_id = rs.novarad_study_id
      AND v.status IN (1, 2);

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0007_ready_studies_gender', 'manual')
ON CONFLICT (version) DO NOTHING;
