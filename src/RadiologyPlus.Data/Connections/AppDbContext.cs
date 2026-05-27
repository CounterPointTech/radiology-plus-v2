using System.Data;
using Microsoft.Extensions.Options;
using Npgsql;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Connections;

public sealed class AppDbOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class AppDbContext : IAppDbContext
{
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly string _connectionString;

    public AppDbContext(IOptions<AppDbOptions> options, ITenantContextAccessor tenantAccessor)
    {
        _tenantAccessor = tenantAccessor;
        _connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("AppDbOptions.ConnectionString is not configured.");
    }

    public async Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var tenant = _tenantAccessor.Require();
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var setTenant = conn.CreateCommand();
        setTenant.CommandText = "SELECT set_config('app.tenant_id', @t, true)";
        setTenant.Parameters.AddWithValue("t", tenant.TenantId.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);

        return conn;
    }

    public async Task<IDbConnection> OpenUnscopedAsync(CancellationToken cancellationToken = default)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}
