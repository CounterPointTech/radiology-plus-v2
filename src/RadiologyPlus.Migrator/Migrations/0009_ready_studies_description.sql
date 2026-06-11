-- Radiology Plus v2.1 — carry study description on the worklist projection
-- So the Tech Validation worklist row can show "CT HEAD W/O CONTRAST" under the
-- patient name instead of a long DICOM UID. Source is ris.orders.description with
-- pacs.studies.anatomical_area as fallback; the reader projects the COALESCE here.
-- Additive; the projector backfills on its next pass.

ALTER TABLE tech_validation.ready_studies ADD COLUMN IF NOT EXISTS study_description TEXT;

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
      AND v.status IN (1, 2);

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0009_ready_studies_description', 'manual')
ON CONFLICT (version) DO NOTHING;
