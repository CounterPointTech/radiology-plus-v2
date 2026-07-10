-- Radiology Plus v2.1 — Per-tenant Microsoft Graph email connection
--
-- Backs the Notifications console's email settings page. Until now the Graph
-- email channel could only read ONE set of credentials from appsettings
-- (Notifications:GraphEmail) — useless for multi-tenant SaaS. This mirrors the
-- tenancy.mmodal_connections pattern: one row per tenant, client secret
-- symmetrically encrypted with the app master key, absent row = channel not
-- configured for that tenant (the channel falls back to appsettings, which
-- stays the dev/single-tenant escape hatch).
--
-- No RLS (read unscoped with an explicit tenant filter, exactly like
-- tenancy.novarad_connections / tenancy.mmodal_connections).

CREATE TABLE tenancy.graph_email_connections (
    tenant_id               UUID PRIMARY KEY REFERENCES tenancy.tenants(tenant_id) ON DELETE CASCADE,
    graph_tenant_id         TEXT        NOT NULL,   -- Entra ID directory (tenant) ID
    client_id               TEXT        NOT NULL,   -- app registration client ID
    client_secret_encrypted BYTEA       NOT NULL,   -- AES-GCM with the app master key
    from_address            TEXT        NOT NULL,   -- mailbox the app sends as
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE tenancy.graph_email_connections IS
    'Per-tenant Microsoft Graph (Office 365) email credentials for the notifications email channel. Client secret is symmetrically encrypted with the app master key. Absent row = tenant not configured (channel falls back to Notifications:GraphEmail appsettings).';

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0021_graph_email_connections', 'manual')
ON CONFLICT (version) DO NOTHING;
