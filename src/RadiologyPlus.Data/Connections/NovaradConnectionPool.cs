using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Connections;

/// <summary>
/// Per-tenant Npgsql data source pool. Connection strings are decrypted on first use
/// and cached in memory; the actual connection pooling is handled by Npgsql per data source.
/// </summary>
public sealed class NovaradConnectionPool : INovaradDbContext, IAsyncDisposable
{
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IAppDbContext _appDb;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<NovaradConnectionPool> _logger;
    private readonly ConcurrentDictionary<Guid, NpgsqlDataSource> _sources = new();

    public NovaradConnectionPool(
        ITenantContextAccessor tenantAccessor,
        IAppDbContext appDb,
        IEncryptionService encryption,
        ILogger<NovaradConnectionPool> logger)
    {
        _tenantAccessor = tenantAccessor;
        _appDb = appDb;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var tenant = _tenantAccessor.Require();
        var source = _sources.GetOrAdd(tenant.TenantId, _ => null!);
        if (source is null)
        {
            source = await BuildDataSourceAsync(tenant.TenantId, cancellationToken);
            _sources[tenant.TenantId] = source;
        }

        return await source.OpenConnectionAsync(cancellationToken);
    }

    private async Task<NpgsqlDataSource> BuildDataSourceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var appConn = (NpgsqlConnection)await _appDb.OpenUnscopedAsync(cancellationToken);
        await using var cmd = appConn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, password_encrypted, use_ssl
            FROM tenancy.novarad_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"No Novarad connection configured for tenant {tenantId}.");

        var host = reader.GetString(0);
        var port = reader.GetInt32(1);
        var database = reader.GetString(2);
        var username = reader.GetString(3);
        var passwordBytes = (byte[])reader.GetValue(4);
        var useSsl = reader.GetBoolean(5);
        var password = _encryption.Decrypt(passwordBytes);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            ApplicationName = "RadiologyPlus",
            SslMode = useSsl ? SslMode.Require : SslMode.Disable,
            Timeout = 15,
            CommandTimeout = 60,
        };

        _logger.LogInformation("Built Novarad data source for tenant {TenantId} -> {Host}/{Db}", tenantId, host, database);
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }

    public void InvalidateTenant(Guid tenantId)
    {
        if (_sources.TryRemove(tenantId, out var source) && source is not null)
        {
            // Existing checked-out connections finish on the old source; new opens rebuild.
            _ = source.DisposeAsync().AsTask();
            _logger.LogInformation("Invalidated cached Novarad data source for tenant {TenantId}.", tenantId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var src in _sources.Values)
        {
            if (src is not null) await src.DisposeAsync();
        }
        _sources.Clear();
    }
}
