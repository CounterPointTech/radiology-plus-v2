using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.Data.Identity;

public sealed class IdentityRepository : IIdentityRepository
{
    private readonly IAppDbContext _db;

    public IdentityRepository(IAppDbContext db) => _db = db;

    public async Task<LocalUserRecord?> GetByUsernameAsync(Guid tenantId, string username, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, tenant_id, username, display_name, email, role, is_local, password_hash, is_active
            FROM identity.users
            WHERE tenant_id = @t AND username = @u
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", username);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LocalUserRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (Role)reader.GetInt16(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8));
    }

    public async Task<LocalUserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, tenant_id, username, display_name, email, role, is_local, password_hash, is_active
            FROM identity.users WHERE user_id = @id
            """;
        cmd.Parameters.AddWithValue("id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LocalUserRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (Role)reader.GetInt16(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8));
    }

    public async Task<Guid> UpsertFederatedAsync(FederatedUserUpsert upsert, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO identity.users (tenant_id, username, display_name, email, role, is_local, is_active)
                VALUES (@t, @u, @dn, @e, @r, FALSE, TRUE)
                ON CONFLICT (tenant_id, username) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    email = EXCLUDED.email,
                    role = EXCLUDED.role,
                    is_active = TRUE,
                    updated_at = NOW()
                RETURNING user_id
                """;
            cmd.Parameters.AddWithValue("t", upsert.TenantId);
            cmd.Parameters.AddWithValue("u", upsert.Username);
            cmd.Parameters.AddWithValue("dn", upsert.DisplayName);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)upsert.Email ?? DBNull.Value });
            cmd.Parameters.AddWithValue("r", (short)upsert.Role);
            var userId = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;

            // Replace facility links
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM identity.user_facilities WHERE user_id = @u";
            del.Parameters.AddWithValue("u", userId);
            await del.ExecuteNonQueryAsync(cancellationToken);

            foreach (var fid in upsert.FacilityIds)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO identity.user_facilities (user_id, facility_id)
                    SELECT @u, facility_id FROM tenancy.facilities
                    WHERE tenant_id = @t AND novarad_facility_id = @nfid
                    ON CONFLICT DO NOTHING
                    """;
                ins.Parameters.AddWithValue("u", userId);
                ins.Parameters.AddWithValue("t", upsert.TenantId);
                ins.Parameters.AddWithValue("nfid", fid);
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return userId;
        }
    }

    public async Task UpdateLastLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE identity.users SET last_login_at = @t WHERE user_id = @id";
        cmd.Parameters.AddWithValue("t", at.UtcDateTime);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> StoreRefreshTokenAsync(
        Guid tenantId, Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity.refresh_tokens (tenant_id, user_id, token_hash, expires_at)
            VALUES (@t, @u, @h, @e)
            RETURNING token_id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("h", tokenHash);
        cmd.Parameters.AddWithValue("e", expiresAt.UtcDateTime);
        return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<(Guid TokenId, Guid UserId, Guid TenantId)?> ValidateRefreshTokenAsync(
        string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token_id, user_id, tenant_id
            FROM identity.refresh_tokens
            WHERE token_hash = @h AND revoked_at IS NULL AND expires_at > NOW()
            """;
        cmd.Parameters.AddWithValue("h", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    public async Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE identity.refresh_tokens SET revoked_at = NOW() WHERE token_id = @id";
        cmd.Parameters.AddWithValue("id", tokenId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetFacilityIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.novarad_facility_id
            FROM identity.user_facilities uf
            JOIN tenancy.facilities f ON f.facility_id = uf.facility_id
            WHERE uf.user_id = @u
            """;
        cmd.Parameters.AddWithValue("u", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }
}
