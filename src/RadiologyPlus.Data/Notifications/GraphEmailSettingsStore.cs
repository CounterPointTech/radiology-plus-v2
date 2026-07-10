using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Notifications.Channels;

namespace RadiologyPlus.Data.Notifications;

/// <summary>
/// Postgres-backed <see cref="IGraphEmailSettingsStore"/>. Encrypts the client secret
/// with the app master key on write; only <see cref="ResolveAsync"/> decrypts it (for
/// the email channel at send time). Mirrors <c>tenancy.mmodal_connections</c> handling.
/// </summary>
public sealed class GraphEmailSettingsStore : IGraphEmailSettingsStore
{
    private readonly IAppDbContext _db;
    private readonly IEncryptionService _encryption;

    public GraphEmailSettingsStore(IAppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<GraphEmailSettingsInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT graph_tenant_id, client_id, (client_secret_encrypted IS NOT NULL) AS has_secret,
                   from_address, updated_at
            FROM tenancy.graph_email_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new GraphEmailSettingsInfo(
            GraphTenantId: reader.GetString(0),
            ClientId: reader.GetString(1),
            HasClientSecret: reader.GetBoolean(2),
            FromAddress: reader.GetString(3),
            UpdatedAt: new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task UpsertAsync(Guid tenantId, GraphEmailSettingsUpsert upsert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        byte[]? encryptedSecret = upsert.ClientSecret is { Length: > 0 }
            ? _encryption.Encrypt(upsert.ClientSecret)
            : null;

        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // COALESCE keeps the stored secret when the form didn't re-enter one. On first insert
        // @secret must be non-null (client_secret_encrypted is NOT NULL) — the endpoint enforces that.
        cmd.CommandText = """
            INSERT INTO tenancy.graph_email_connections
                (tenant_id, graph_tenant_id, client_id, client_secret_encrypted, from_address, updated_at)
            VALUES (@t, @gt, @cid, @secret, @from, NOW())
            ON CONFLICT (tenant_id) DO UPDATE SET
                graph_tenant_id         = EXCLUDED.graph_tenant_id,
                client_id               = EXCLUDED.client_id,
                client_secret_encrypted = COALESCE(EXCLUDED.client_secret_encrypted, tenancy.graph_email_connections.client_secret_encrypted),
                from_address            = EXCLUDED.from_address,
                updated_at              = NOW()
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("gt", upsert.GraphTenantId);
        cmd.Parameters.AddWithValue("cid", upsert.ClientId);
        cmd.Parameters.Add(new NpgsqlParameter("secret", NpgsqlDbType.Bytea) { Value = (object?)encryptedSecret ?? DBNull.Value });
        cmd.Parameters.AddWithValue("from", upsert.FromAddress);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tenancy.graph_email_connections WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<GraphEmailOptions?> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT graph_tenant_id, client_id, client_secret_encrypted, from_address
            FROM tenancy.graph_email_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var secretBytes = (byte[])reader.GetValue(2);
        return new GraphEmailOptions
        {
            TenantId = reader.GetString(0),
            ClientId = reader.GetString(1),
            ClientSecret = _encryption.Decrypt(secretBytes),
            FromAddress = reader.GetString(3),
        };
    }
}
