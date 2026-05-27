-- Radiology Plus v2.1 — Seed default reason quick-fill templates (P1.9)
-- Gives every existing tenant a starter set of canned reasons for wizard step 3.
-- Idempotent via the UNIQUE (tenant_id, label) constraint. NRS/Admin can edit/add
-- more from the in-app management screen. Runs under system_bypass RLS (the migrator
-- has no app.tenant_id set, so core.current_tenant() is NULL).

INSERT INTO tech_validation.tech_notes_templates (tenant_id, label, body, sort_order)
SELECT t.tenant_id, x.label, x.body, x.sort_order
FROM tenancy.tenants t
CROSS JOIN (VALUES
    ('Demographics confirmed', 'Patient demographics confirmed against the order; no changes needed.', 10),
    ('Demographics corrected', 'Patient demographics corrected to match the order/ID.', 20),
    ('Order matched',          'Study matched to the correct order.', 30),
    ('Prior compared',         'Relevant prior study identified and flagged for comparison.', 40),
    ('No prior available',     'No relevant prior study available for comparison.', 50),
    ('Reassigned patient',     'Study reassigned to the correct patient.', 60),
    ('Duplicate study',        'Duplicate/erroneous study; flagged for review.', 70)
) AS x(label, body, sort_order)
ON CONFLICT (tenant_id, label) DO NOTHING;

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0006_tech_notes_templates_seed', 'manual')
ON CONFLICT (version) DO NOTHING;
