-- Radiology Plus v2.1 — relax wizard step range to 1..6
-- A new "Comparisons" wizard step was inserted at position 3. WizardStep enum now
-- runs 1..6 (Study, Order, Comparisons, Reason, Review, DoTheDo). The existing
-- check constraint (BETWEEN 1 AND 5) has to be relaxed before the API will accept
-- a step 6 finalize.

ALTER TABLE tech_validation.validations
    DROP CONSTRAINT IF EXISTS validations_current_step_check;

ALTER TABLE tech_validation.validations
    ADD CONSTRAINT validations_current_step_check
    CHECK (current_step BETWEEN 1 AND 6);

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0010_wizard_step_six', 'manual')
ON CONFLICT (version) DO NOTHING;
