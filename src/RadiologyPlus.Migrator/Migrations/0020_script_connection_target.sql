-- Radiology Plus v2.1 — Script connection target
-- Which system a stored script executes against. Before this, the engine had no
-- way to resolve a connection for scheduled/manual runs of DB scripts (the
-- executors require one for pgsql/tsql), so every stored DB script failed.
--   appdb   -> our Postgres (ConnectionStrings:AppDb)
--   novarad -> the tenant's tenancy.novarad_connections row (Postgres)
--   mmodal  -> the tenant's tenancy.mmodal_connections row (SQL Server)
--   none    -> no connection (powershell / batch)

ALTER TABLE scripting.scripts
    ADD COLUMN connection_target TEXT NOT NULL DEFAULT 'appdb'
    CONSTRAINT chk_scripts_connection_target
    CHECK (connection_target IN ('appdb', 'novarad', 'mmodal', 'none'));

COMMENT ON COLUMN scripting.scripts.connection_target IS
    'Connection a DB script executes against: appdb | novarad | mmodal | none (powershell/batch).';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0020_script_connection_target', 'manual')
ON CONFLICT (version) DO NOTHING;
