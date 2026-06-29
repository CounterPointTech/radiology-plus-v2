-- Radiology Plus v2.1 — RVU overrides: site-specific overrides
--
-- billing.rvu_overrides (0003) was tenant-wide (one override per CPT/year for the
-- whole tenant). It carried a never-used facility_id column keyed to
-- tenancy.facilities. Customers need the SAME CPT to credit a DIFFERENT work RVU at
-- different sites, and (as with the crosswalk in 0015) the real unit of "where a
-- report was read" is the raw Novarad site_code, not a tenancy.facilities id — the
-- customer has 166 distinct ris.orders.site_codes but only 1 placeholder facility
-- row, so facility_id keying is inert. This adds an optional SITE scope keyed by the
-- raw site_code, mirroring 0015's crosswalk change:
--   site_code NULL  = tenant-wide override (today's behavior)
--   site_code set   = site-specific override
-- The vestigial facility_id column is left in place (always NULL) to keep this
-- migration purely additive; site_code supersedes it for all scoping.
--
-- Resolution at reconciliation is site-first with tenant fallback: a site override
-- wins over the tenant-wide override at that site, which wins over CMS, then the
-- master sheet (site > tenant > CMS > master).
--
-- Existing rows keep site_code NULL and stay tenant-wide. No backfill.
-- RLS is unchanged — a column add inherits the table's existing policies.

ALTER TABLE billing.rvu_overrides
    ADD COLUMN site_code TEXT;

COMMENT ON COLUMN billing.rvu_overrides.site_code IS
    'NULL = tenant-wide override; non-NULL = site-specific (raw Novarad ris.orders.site_code). A site override wins over the tenant-wide override at that site during reconciliation.';

-- Re-key the two partial uniques off site_code (was facility_id). One override per
-- (code, site) and one tenant-wide default per code. Existing data has site_code
-- NULL, so it already satisfies the _all partial — no violation on create.
DROP INDEX IF EXISTS billing.uq_rvu_override_tenant_year_cpt_facility;
DROP INDEX IF EXISTS billing.uq_rvu_override_tenant_year_cpt_all;

CREATE UNIQUE INDEX uq_rvu_override_tenant_year_cpt_site
    ON billing.rvu_overrides (tenant_id, year, cpt_code, site_code)
    WHERE site_code IS NOT NULL;

CREATE UNIQUE INDEX uq_rvu_override_tenant_year_cpt_all
    ON billing.rvu_overrides (tenant_id, year, cpt_code)
    WHERE site_code IS NULL;

COMMENT ON TABLE billing.rvu_overrides IS
    'Per-tenant RVU overrides. NULL site_code = tenant-wide override; non-NULL = site-specific (raw Novarad site_code). The legacy facility_id column is unused (see migration 0016).';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0016_rvu_override_site', 'manual')
ON CONFLICT (version) DO NOTHING;
