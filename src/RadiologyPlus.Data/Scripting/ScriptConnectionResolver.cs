using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

/// <summary>
/// Builds connection strings for script runs from the same per-tenant encrypted
/// rows the rest of the app uses (tenancy.novarad_connections /
/// tenancy.mmodal_connections) plus the app database itself.
/// </summary>
public sealed class ScriptConnectionResolver : IScriptConnectionResolver
{
    private readonly IAppDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly AppDbOptions _appDbOptions;

    public ScriptConnectionResolver(IAppDbContext db, IEncryptionService encryption, IOptions<AppDbOptions> appDbOptions)
    {
        _db = db;
        _encryption = encryption;
        _appDbOptions = appDbOptions.Value;
    }

    public async Task<string?> ResolveAsync(Guid tenantId, string connectionTarget, CancellationToken cancellationToken = default)
    {
        switch (connectionTarget)
        {
            case "none":
                return null;
            case "appdb":
                return _appDbOptions.ConnectionString;
            case "novarad":
                return await BuildNovaradAsync(tenantId, cancellationToken);
            case "mmodal":
                return await BuildMModalAsync(tenantId, cancellationToken);
            default:
                throw new InvalidOperationException($"Unknown script connection target '{connectionTarget}'.");
        }
    }

    private async Task<string> BuildNovaradAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Same row + builder shape as NovaradConnectionPool (a pooled data source is
        // unnecessary here — the executor opens one connection per run).
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, password_encrypted, use_ssl
            FROM tenancy.novarad_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("This script targets Novarad, but no Novarad connection is configured for this site.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = reader.GetString(0),
            Port = reader.GetInt32(1),
            Database = reader.GetString(2),
            Username = reader.GetString(3),
            Password = _encryption.Decrypt((byte[])reader.GetValue(4)),
            ApplicationName = "RadiologyPlus.Scripts",
            SslMode = reader.GetBoolean(5) ? SslMode.Require : SslMode.Disable,
            Timeout = 15,
        };
        return builder.ConnectionString;
    }

    private async Task<string> BuildMModalAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, password_encrypted, use_ssl, trust_server_cert
            FROM tenancy.mmodal_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("This script targets M*Modal, but no M*Modal connection is configured for this site.");

        var host = reader.GetString(0);
        var port = reader.GetInt32(1);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = port == 1433 ? host : $"{host},{port}",
            InitialCatalog = reader.GetString(2),
            UserID = reader.GetString(3),
            Password = _encryption.Decrypt((byte[])reader.GetValue(4)),
            ApplicationName = "RadiologyPlus.Scripts",
            Encrypt = reader.GetBoolean(5),
            TrustServerCertificate = reader.GetBoolean(6),
            ConnectTimeout = 15,
        };
        return builder.ConnectionString;
    }
}
