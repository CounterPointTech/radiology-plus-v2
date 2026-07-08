using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Postgres-backed <see cref="IMModalConnectionStore"/>. Encrypts the password with the app
/// master key on write; never returns it. Mirrors <c>tenancy.novarad_connections</c> handling.
/// </summary>
public sealed class MModalConnectionStore : IMModalConnectionStore
{
    private readonly IAppDbContext _db;
    private readonly IEncryptionService _encryption;

    public MModalConnectionStore(IAppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<MModalConnectionInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, use_ssl, trust_server_cert, issuer_key,
                   (password_encrypted IS NOT NULL) AS has_password, updated_at
            FROM tenancy.mmodal_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new MModalConnectionInfo(
            Host: reader.GetString(0),
            Port: reader.GetInt32(1),
            Database: reader.GetString(2),
            Username: reader.GetString(3),
            UseSsl: reader.GetBoolean(4),
            TrustServerCert: reader.GetBoolean(5),
            IssuerKey: reader.IsDBNull(6) ? null : reader.GetGuid(6),
            HasPassword: reader.GetBoolean(7),
            UpdatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
    }

    public async Task UpsertAsync(Guid tenantId, MModalConnectionUpsert upsert, CancellationToken cancellationToken = default)
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
            INSERT INTO tenancy.mmodal_connections
                (tenant_id, host, port, database_name, username, password_encrypted,
                 use_ssl, trust_server_cert, issuer_key, updated_at)
            VALUES (@t, @h, @p, @db, @u, @pw, @ssl, @trust, @issuer, NOW())
            ON CONFLICT (tenant_id) DO UPDATE SET
                host               = EXCLUDED.host,
                port               = EXCLUDED.port,
                database_name      = EXCLUDED.database_name,
                username           = EXCLUDED.username,
                password_encrypted = COALESCE(EXCLUDED.password_encrypted, tenancy.mmodal_connections.password_encrypted),
                use_ssl            = EXCLUDED.use_ssl,
                trust_server_cert  = EXCLUDED.trust_server_cert,
                issuer_key         = EXCLUDED.issuer_key,
                updated_at         = NOW()
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("h", upsert.Host);
        cmd.Parameters.AddWithValue("p", upsert.Port);
        cmd.Parameters.AddWithValue("db", upsert.Database);
        cmd.Parameters.AddWithValue("u", upsert.Username);
        cmd.Parameters.Add(new NpgsqlParameter("pw", NpgsqlDbType.Bytea) { Value = (object?)encryptedPw ?? DBNull.Value });
        cmd.Parameters.AddWithValue("ssl", upsert.UseSsl);
        cmd.Parameters.AddWithValue("trust", upsert.TrustServerCert);
        cmd.Parameters.Add(new NpgsqlParameter("issuer", NpgsqlDbType.Uuid) { Value = (object?)upsert.IssuerKey ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tenancy.mmodal_connections WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
