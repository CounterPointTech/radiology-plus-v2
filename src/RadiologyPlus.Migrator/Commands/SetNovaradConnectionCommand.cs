using Microsoft.Extensions.Configuration;
using Npgsql;
using RadiologyPlus.Common.Encryption;
using Serilog;

namespace RadiologyPlus.Migrator;

/// <summary>
/// Repoint an existing tenant's Novarad connection. Partial update: only the
/// flags you pass change; everything else keeps its current value. Use this to
/// move a tenant to a different Novarad DB, rotate the connection password, or
/// flip SSL without re-running init-tenant.
/// </summary>
internal sealed class SetNovaradConnectionCommand
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;

    public SetNovaradConnectionCommand(string connectionString, IConfiguration config)
    {
        _connectionString = connectionString;
        _config = config;
    }

    public async Task<int> RunAsync(IReadOnlyDictionary<string, string> flags)
    {
        var tenantCode = flags.Required("tenant");

        var hasHost = flags.TryGetValue("novarad-host", out var host);
        var hasPort = flags.TryGetValue("novarad-port", out var portRaw);
        var hasDb = flags.TryGetValue("novarad-db", out var database);
        var hasUser = flags.TryGetValue("novarad-user", out var username);
        var hasPassword = flags.TryGetValue("novarad-password", out var password);
        var hasSsl = flags.TryGetValue("use-ssl", out var sslRaw);
        var hasAuditTable = flags.TryGetValue("audit-table", out var auditTable);

        if (!(hasHost || hasPort || hasDb || hasUser || hasPassword || hasSsl || hasAuditTable))
            throw new UsageException(
                "Nothing to change. Pass at least one of --novarad-host, --novarad-port, " +
                "--novarad-db, --novarad-user, --novarad-password, --use-ssl, --audit-table.");

        int? port = null;
        if (hasPort)
            port = int.TryParse(portRaw, out var p)
                ? p
                : throw new UsageException("--novarad-port must be an integer.");

        bool? useSsl = null;
        if (hasSsl)
            useSsl = bool.TryParse(sslRaw, out var s)
                ? s
                : throw new UsageException("--use-ssl must be true or false.");

        // Only the encryption key is needed, and only when rotating the password.
        byte[]? encryptedPw = null;
        if (hasPassword)
        {
            var encryptionKey = _config["Encryption:Key"]
                ?? throw new UsageException("Encryption:Key is required to set a password (set RADPLUS_Encryption__Key).");
            encryptedPw = new AesGcmEncryptionService(encryptionKey).Encrypt(password!);
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            Guid tenantId;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT tenant_id FROM tenancy.tenants WHERE code = @c";
                cmd.Parameters.AddWithValue("c", tenantCode);
                var result = await cmd.ExecuteScalarAsync();
                if (result is null or DBNull)
                    throw new UsageException($"No tenant found with code '{tenantCode}'. Run 'init-tenant' first.");
                tenantId = (Guid)result;
            }

            // Ensure a connection row exists — this is a repoint, not a create.
            await using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM tenancy.novarad_connections WHERE tenant_id = @t";
                check.Parameters.AddWithValue("t", tenantId);
                if (await check.ExecuteScalarAsync() is null)
                    throw new UsageException(
                        $"Tenant '{tenantCode}' has no Novarad connection yet. Run 'init-tenant' to create one first.");
            }

            // COALESCE keeps the existing column value when the flag wasn't passed.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tenancy.novarad_connections SET
                        host               = COALESCE(@h, host),
                        port               = COALESCE(@p, port),
                        database_name      = COALESCE(@db, database_name),
                        username           = COALESCE(@u, username),
                        password_encrypted = COALESCE(@pw, password_encrypted),
                        use_ssl            = COALESCE(@ssl, use_ssl),
                        novarad_audit_table = COALESCE(@audit, novarad_audit_table),
                        updated_at         = NOW()
                    WHERE tenant_id = @t
                    """;
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("h", (object?)(hasHost ? host : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("p", (object?)port ?? DBNull.Value);
                cmd.Parameters.AddWithValue("db", (object?)(hasDb ? database : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("u", (object?)(hasUser ? username : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pw", (object?)encryptedPw ?? DBNull.Value);
                cmd.Parameters.AddWithValue("ssl", (object?)useSsl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("audit", (object?)(hasAuditTable ? auditTable : null) ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            // Read back so the operator can see the resulting connection.
            string finalHost, finalDb, finalUser, finalAudit;
            int finalPort, pwBytes;
            bool finalSsl;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT host, port, database_name, username, use_ssl, octet_length(password_encrypted),
                           COALESCE(novarad_audit_table, '(default)')
                    FROM tenancy.novarad_connections WHERE tenant_id = @t
                    """;
                read.Parameters.AddWithValue("t", tenantId);
                await using var r = await read.ExecuteReaderAsync();
                await r.ReadAsync();
                finalHost = r.GetString(0);
                finalPort = r.GetInt32(1);
                finalDb = r.GetString(2);
                finalUser = r.GetString(3);
                finalSsl = r.GetBoolean(4);
                pwBytes = r.GetInt32(5);
                finalAudit = r.GetString(6);
            }

            await tx.CommitAsync();

            Log.Information(
                "Novarad connection for tenant '{Tenant}' set to {User}@{Host}:{Port}/{Db} (ssl={Ssl}, pw_bytes={PwBytes}, audit_table={Audit}). " +
                "Restart the API — NovaradConnectionPool caches the data source per tenant.",
                tenantCode, finalUser, finalHost, finalPort, finalDb, finalSsl, pwBytes, finalAudit);
            return 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
