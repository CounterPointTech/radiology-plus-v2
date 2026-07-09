-- Radiology Plus v2.1 — Billing crosswalk: site-specific mappings
--
-- The service_code → CPT crosswalk (0008) was tenant-wide: one mapping per code
-- for the whole tenant. Customers need the SAME Novarad service code to resolve to
-- a DIFFERENT CPT at different sites (a local code means different procedures by
-- site). This adds an optional SITE scope keyed by the raw Novarad site_code (the
-- value that appears on every order / reconciliation line):
--   site_code NULL  = tenant-wide default
--   site_code set   = site-specific
-- Keyed by site_code (not a tenancy.facilities id) because the customer's sites
-- live as raw site_code strings on ris.orders (166 distinct) and are not all
-- onboarded into tenancy.facilities. Reconciliation already carries site_code on
-- every line, so resolution needs no facility lookup.
--
-- Resolution is site-first with tenant-wide fallback; a site row (approved OR
-- suppressed) always wins over the tenant default at that site.
--
-- Existing rows keep site_code NULL and become tenant-wide defaults. No backfill.
-- RLS is unchanged — a column add inherits the table's existing policies.

ALTER TABLE billing.service_code_crosswalk
    ADD COLUMN site_code TEXT;

COMMENT ON COLUMN billing.service_code_crosswalk.site_code IS
    'NULL = tenant-wide default; non-NULL = site-specific (raw Novarad ris.orders.site_code). A site row (any status) wins over the tenant-wide default at resolution.';

-- Two partial uniques replace the single (tenant_id, service_code) guard: one
-- mapping per (code, site) and one tenant-wide default per code. Existing data
-- already satisfies the _all partial, so no violation.
DROP INDEX IF EXISTS billing.uq_crosswalk_tenant_service;

CREATE UNIQUE INDEX uq_crosswalk_tenant_service_site
    ON billing.service_code_crosswalk (tenant_id, service_code, site_code)
    WHERE site_code IS NOT NULL;

CREATE UNIQUE INDEX uq_crosswalk_tenant_service_all
    ON billing.service_code_crosswalk (tenant_id, service_code)
    WHERE site_code IS NULL;

-- Rebuild the matcher's hot-path index with site_code trailing (the loader now
-- reads both scopes and both statuses for a tenant).
DROP INDEX IF EXISTS billing.idx_crosswalk_tenant_status;
CREATE INDEX idx_crosswalk_tenant_status
    ON billing.service_code_crosswalk (tenant_id, status, site_code);

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0015_crosswalk_facility', 'manual')
ON CONFLICT (version) DO NOTHING;
