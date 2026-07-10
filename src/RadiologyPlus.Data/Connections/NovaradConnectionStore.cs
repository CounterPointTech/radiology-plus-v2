using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Connections;

/// <summary>
/// Postgres-backed <see cref="INovaradConnectionStore"/>. Encrypts the password with
/// the app master key on write; never returns it. Mirrors MModalConnectionStore —
/// the runtime consumers (NovaradConnectionPool, ScriptConnectionResolver) read the
/// same row directly.
/// </summary>
public sealed class NovaradConnectionStore : INovaradConnectionStore
{
    private readonly IAppDbContext _db;
    private readonly IEncryptionService _encryption;

    public NovaradConnectionStore(IAppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<NovaradConnectionInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, use_ssl, novarad_audit_table, notes,
                   (password_encrypted IS NOT NULL) AS has_password, updated_at
            FROM tenancy.novarad_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new NovaradConnectionInfo(
            Host: reader.GetString(0),
            Port: reader.GetInt32(1),
            Database: reader.GetString(2),
            Username: reader.GetString(3),
            UseSsl: reader.GetBoolean(4),
            NovaradAuditTable: reader.GetString(5),
            Notes: reader.IsDBNull(6) ? null : reader.GetString(6),
            HasPassword: reader.GetBoolean(7),
            UpdatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
    }

    public async Task UpsertAsync(Guid tenantId, NovaradConnectionUpsert upsert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        byte[]? encryptedPw = upsert.Password is { Length: > 0 }
            ? _encryption.Encrypt(upsert.Password)
            : null;

        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // COALESCE keeps the stored password when the form didn't re-enter one. On first insert
        // @pw must be non-null (password_encrypted is NOT NULL) — the endpoint enforces that.
        cmd.CommandText = """
            INSERT INTO tenancy.novarad_connections
                (tenant_id, host, port, database_name, username, password_encrypted,
                 use_ssl, novarad_audit_table, notes, updated_at)
            VALUES (@t, @h, @p, @db, @u, @pw, @ssl, @audit, @notes, NOW())
            ON CONFLICT (tenant_id) DO UPDATE SET
                host                = EXCLUDED.host,
                port                = EXCLUDED.port,
                database_name       = EXCLUDED.database_name,
                username            = EXCLUDED.username,
                password_encrypted  = COALESCE(EXCLUDED.password_encrypted, tenancy.novarad_connections.password_encrypted),
                use_ssl             = EXCLUDED.use_ssl,
                novarad_audit_table = EXCLUDED.novarad_audit_table,
                notes               = EXCLUDED.notes,
                updated_at          = NOW()
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("h", upsert.Host);
        cmd.Parameters.AddWithValue("p", upsert.Port);
        cmd.Parameters.AddWithValue("db", upsert.Database);
        cmd.Parameters.AddWithValue("u", upsert.Username);
        cmd.Parameters.Add(new NpgsqlParameter("pw", NpgsqlDbType.Bytea) { Value = (object?)encryptedPw ?? DBNull.Value });
        cmd.Parameters.AddWithValue("ssl", upsert.UseSsl);
        cmd.Parameters.AddWithValue("audit", upsert.NovaradAuditTable);
        cmd.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text) { Value = (object?)upsert.Notes ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tenancy.novarad_connections WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
