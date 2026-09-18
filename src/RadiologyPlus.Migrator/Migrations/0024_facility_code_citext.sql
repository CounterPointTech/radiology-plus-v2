-- 0024: facility codes are case-insensitive.
--
-- tenancy.facilities.code is populated from Novarad's shared.facilities.name, which
-- is citext, but was stored as TEXT and compared with `=` at login. A user therefore
-- had to type the facility exactly as Novarad happened to capitalise it
-- ("Unknown Facility" worked, "unknown facility" was rejected as unknown).
-- tenancy.tenants.code has been CITEXT since 0001; this brings facilities in line.
--
-- Deliberately NOT adding UNIQUE (tenant_id, code): Novarad facility names are not
-- guaranteed unique, and the import upserts on (tenant_id, novarad_facility_id).

ALTER TABLE tenancy.facilities
    ALTER COLUMN code TYPE CITEXT;
