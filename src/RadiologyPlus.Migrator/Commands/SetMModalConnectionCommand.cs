using Microsoft.Extensions.Configuration;
using Npgsql;
using RadiologyPlus.Common.Encryption;
using Serilog;

namespace RadiologyPlus.Migrator;

/// <summary>
/// Create or repoint a tenant's M*Modal ClinicalDataStore connection — the target of the
/// RVU write-back (<c>tenancy.mmodal_connections</c>). Unlike Novarad there is no
/// init-tenant step that pre-creates the row, so this is an upsert: on first run
/// <c>--host/--user/--password</c> are required and the rest take table defaults;
/// afterwards it is a partial update (pass only what changes). The password is AES-GCM
/// encrypted at rest with the app master key. No API restart needed — the sink reads the
/// connection row per request.
/// </summary>
internal sealed class SetMModalConnectionCommand
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;

    public SetMModalConnectionCommand(string connectionString, IConfiguration config)
    {
        _connectionString = connectionString;
        _config = config;
    }

    public async Task<int> RunAsync(IReadOnlyDictionary<string, string> flags)
    {
        var tenantCode = flags.Required("tenant");

        var hasHost = flags.TryGetValue("host", out var host);
        var hasPort = flags.TryGetValue("port", out var portRaw);
        var hasDb = flags.TryGetValue("db", out var database);
        var hasUser = flags.TryGetValue("user", out var username);
        var hasPassword = flags.TryGetValue("password", out var password);
        var hasSsl = flags.TryGetValue("use-ssl", out var sslRaw);
        var hasTrust = flags.TryGetValue("trust-server-cert", out var trustRaw);
        var hasIssuer = flags.TryGetValue("issuer-key", out var issuerRaw);

        int? port = null;
        if (hasPort)
            port = int.TryParse(portRaw, out var p) ? p : throw new UsageException("--port must be an integer.");

        bool? useSsl = null;
        if (hasSsl)
            useSsl = bool.TryParse(sslRaw, out var s) ? s : throw new UsageException("--use-ssl must be true or false.");

        bool? trustCert = null;
        if (hasTrust)
            trustCert = bool.TryParse(trustRaw, out var t) ? t : throw new UsageException("--trust-server-cert must be true or false.");

        Guid? issuerKey = null;
        if (hasIssuer)
            issuerKey = Guid.TryParse(issuerRaw, out var g) ? g : throw new UsageException("--issuer-key must be a GUID.");

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

            bool exists;
            await using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM tenancy.mmodal_connections WHERE tenant_id = @t";
                check.Parameters.AddWithValue("t", tenantId);
                exists = await check.ExecuteScalarAsync() is not null;
            }

            if (!exists)
            {
                // Create: host/user/password are required; the rest take the table defaults.
                if (!hasHost || !hasUser || !hasPassword)
                    throw new UsageException(
                        "Creating a new M*Modal connection requires --host, --user and --password " +
                        "(optional: --port [1433], --db [ClinicalDataStore], --use-ssl [true], " +
                        "--trust-server-cert [true], --issuer-key).");

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tenancy.mmodal_connections
                        (tenant_id, host, port, database_name, username, password_encrypted,
                         use_ssl, trust_server_cert, issuer_key)
                    VALUES (@t, @h, COALESCE(@p, 1433), COALESCE(@db, 'ClinicalDataStore'),
                            @u, @pw, COALESCE(@ssl, TRUE), COALESCE(@trust, TRUE), @issuer)
                    """;
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("h", host!);
                cmd.Parameters.AddWithValue("p", (object?)port ?? DBNull.Value);
                cmd.Parameters.AddWithValue("db", (object?)(hasDb ? database : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("u", username!);
                cmd.Parameters.AddWithValue("pw", encryptedPw!);
                cmd.Parameters.AddWithValue("ssl", (object?)useSsl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("trust", (object?)trustCert ?? DBNull.Value);
                cmd.Parameters.AddWithValue("issuer", (object?)issuerKey ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Repoint: only the flags you pass change (COALESCE keeps current values).
                if (!(hasHost || hasPort || hasDb || hasUser || hasPassword || hasSsl || hasTrust || hasIssuer))
                    throw new UsageException(
                        "Nothing to change. Pass at least one of --host, --port, --db, --user, " +
                        "--password, --use-ssl, --trust-server-cert, --issuer-key.");

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tenancy.mmodal_connections SET
                        host               = COALESCE(@h, host),
                        port               = COALESCE(@p, port),
                        database_name      = COALESCE(@db, database_name),
                        username           = COALESCE(@u, username),
                        password_encrypted = COALESCE(@pw, password_encrypted),
                        use_ssl            = COALESCE(@ssl, use_ssl),
                        trust_server_cert  = COALESCE(@trust, trust_server_cert),
                        issuer_key         = COALESCE(@issuer, issuer_key),
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
                cmd.Parameters.AddWithValue("trust", (object?)trustCert ?? DBNull.Value);
                cmd.Parameters.AddWithValue("issuer", (object?)issuerKey ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            // Read back so the operator can see the resulting connection.
            string finalHost, finalDb, finalUser, finalIssuer;
            int finalPort, pwBytes;
            bool finalSsl, finalTrust;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT host, port, database_name, username, use_ssl, trust_server_cert,
                           octet_length(password_encrypted), COALESCE(issuer_key::text, '(all issuers)')
                    FROM tenancy.mmodal_connections WHERE tenant_id = @t
                    """;
                read.Parameters.AddWithValue("t", tenantId);
                await using var r = await read.ExecuteReaderAsync();
                await r.ReadAsync();
                finalHost = r.GetString(0);
                finalPort = r.GetInt32(1);
                finalDb = r.GetString(2);
                finalUser = r.GetString(3);
                finalSsl = r.GetBoolean(4);
                finalTrust = r.GetBoolean(5);
                pwBytes = r.GetInt32(6);
                finalIssuer = r.GetString(7);
            }

            await tx.CommitAsync();

            Log.Information(
                "M*Modal connection for tenant '{Tenant}' {Action}: {User}@{Host}:{Port}/{Db} " +
                "(ssl={Ssl}, trust_cert={Trust}, pw_bytes={PwBytes}, issuer={Issuer}). " +
                "Takes effect immediately — the write-back sink reads this row per request.",
                tenantCode, exists ? "updated" : "created",
                finalUser, finalHost, finalPort, finalDb, finalSsl, finalTrust, pwBytes, finalIssuer);
            return 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
