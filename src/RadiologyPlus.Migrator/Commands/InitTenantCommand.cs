using Microsoft.Extensions.Configuration;
using Npgsql;
using RadiologyPlus.Common.Encryption;
using Serilog;

namespace RadiologyPlus.Migrator;

internal sealed class InitTenantCommand
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;

    public InitTenantCommand(string connectionString, IConfiguration config)
    {
        _connectionString = connectionString;
        _config = config;
    }

    public async Task<int> RunAsync(IReadOnlyDictionary<string, string> flags)
    {
        var code = flags.Required("code");
        var name = flags.Required("name");
        var host = flags.Required("novarad-host");
        var port = flags.OptionalInt("novarad-port", 5432);
        var database = flags.Required("novarad-db");
        var username = flags.Required("novarad-user");
        var password = flags.Required("novarad-password");
        var useSsl = flags.OptionalBool("use-ssl", fallback: true);

        var encryptionKey = _config["Encryption:Key"]
            ?? throw new UsageException("Encryption:Key is required (set RADPLUS_Encryption__Key).");
        var encryption = new AesGcmEncryptionService(encryptionKey);
        var encryptedPw = encryption.Encrypt(password);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // 1) tenants row (idempotent on code)
            Guid tenantId;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tenancy.tenants (code, display_name)
                    VALUES (@code, @name)
                    ON CONFLICT (code) DO UPDATE SET display_name = EXCLUDED.display_name, updated_at = NOW()
                    RETURNING tenant_id
                    """;
                cmd.Parameters.AddWithValue("code", code);
                cmd.Parameters.AddWithValue("name", name);
                tenantId = (Guid)(await cmd.ExecuteScalarAsync())!;
            }

            // 2) novarad_connections row (upsert)
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tenancy.novarad_connections
                        (tenant_id, host, port, database_name, username, password_encrypted, use_ssl, updated_at)
                    VALUES (@t, @h, @p, @db, @u, @pw, @ssl, NOW())
                    ON CONFLICT (tenant_id) DO UPDATE SET
                        host = EXCLUDED.host,
                        port = EXCLUDED.port,
                        database_name = EXCLUDED.database_name,
                        username = EXCLUDED.username,
                        password_encrypted = EXCLUDED.password_encrypted,
                        use_ssl = EXCLUDED.use_ssl,
                        updated_at = NOW()
                    """;
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("h", host);
                cmd.Parameters.AddWithValue("p", port);
                cmd.Parameters.AddWithValue("db", database);
                cmd.Parameters.AddWithValue("u", username);
                cmd.Parameters.AddWithValue("pw", encryptedPw);
                cmd.Parameters.AddWithValue("ssl", useSsl);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            Log.Information("Tenant {Code} ({Name}) ready. tenant_id={TenantId}, novarad={Host}:{Port}/{Db}",
                code, name, tenantId, host, port, database);
            return 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
