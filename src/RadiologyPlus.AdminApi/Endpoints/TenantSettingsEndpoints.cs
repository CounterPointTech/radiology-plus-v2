using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Tenant settings surface (NRS/Admin): the Novarad connection that powers
/// federated login, billing reads, and NRS scripting — plus read-only tenant info.
/// The connection pool is invalidated on save/delete so changes bite immediately.
/// </summary>
public static class TenantSettingsEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings")
            .WithTags("Settings")
            .RequireAuthorization();

        group.MapGet("/tenant", GetTenantAsync).WithName("SettingsTenant");
        group.MapGet("/novarad", GetConnectionAsync).WithName("SettingsNovarad");
        group.MapPut("/novarad", SaveConnectionAsync).WithName("SettingsNovaradSave");
        group.MapDelete("/novarad", DeleteConnectionAsync).WithName("SettingsNovaradDelete");
        group.MapPost("/novarad/test", TestConnectionAsync).WithName("SettingsNovaradTest");

        return app;
    }

    private static async Task<IResult> GetTenantAsync(
        ICurrentUser currentUser, ITenantRepository tenants, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var tenant = await tenants.GetByIdAsync(user.TenantId, ct);
        return tenant is null
            ? Results.NotFound(new { error = "Tenant not found." })
            : Results.Ok(new { tenant.Code, tenant.DisplayName, tenant.IsActive });
    }

    private static async Task<IResult> GetConnectionAsync(
        ICurrentUser currentUser, INovaradConnectionStore store, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var settings = await store.GetAsync(user.TenantId, ct);
        return Results.Ok(new { configured = settings is not null, settings });
    }

    private static async Task<IResult> SaveConnectionAsync(
        NovaradConnectionSaveRequest req,
        ICurrentUser currentUser,
        INovaradConnectionStore store,
        INovaradDbContext pool,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Host))
            return Results.BadRequest(new { error = "host is required." });
        if (string.IsNullOrWhiteSpace(req.Database))
            return Results.BadRequest(new { error = "database is required." });
        if (string.IsNullOrWhiteSpace(req.Username))
            return Results.BadRequest(new { error = "username is required." });
        if (req.Port is < 1 or > 65_535)
            return Results.BadRequest(new { error = "port must be between 1 and 65535." });

        var password = string.IsNullOrWhiteSpace(req.Password) ? null : req.Password;
        if (password is null && await store.GetAsync(user.TenantId, ct) is null)
            return Results.BadRequest(new { error = "password is required on first setup." });

        await store.UpsertAsync(user.TenantId, new NovaradConnectionUpsert(
            req.Host.Trim(), req.Port, req.Database.Trim(), req.Username.Trim(), password,
            req.UseSsl,
            string.IsNullOrWhiteSpace(req.NovaradAuditTable) ? "object_store.audit" : req.NovaradAuditTable.Trim(),
            string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim()), ct);

        // The pool caches the decrypted data source per tenant — drop it so the
        // next Novarad read (or the test button) uses what was just saved.
        pool.InvalidateTenant(user.TenantId);

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
            $"tenancy.novarad_connections: saved ({req.Host.Trim()}/{req.Database.Trim()}, password {(password is null ? "kept" : "replaced")})",
            http, ct);

        var settings = await store.GetAsync(user.TenantId, ct);
        return Results.Ok(new { configured = true, settings });
    }

    private static async Task<IResult> DeleteConnectionAsync(
        ICurrentUser currentUser,
        INovaradConnectionStore store,
        INovaradDbContext pool,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var removed = await store.DeleteAsync(user.TenantId, ct);
        if (!removed) return Results.NotFound(new { error = "No Novarad connection is configured." });

        pool.InvalidateTenant(user.TenantId);
        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            "tenancy.novarad_connections: removed", http, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestConnectionAsync(
        ICurrentUser currentUser,
        INovaradDbContext pool,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var started = DateTimeOffset.UtcNow;
        try
        {
            await using var conn = (NpgsqlConnection)await pool.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version()";
            var version = (string?)await cmd.ExecuteScalarAsync(ct);
            var ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
                $"novarad connection test: success in {ms}ms", http, ct);
            return Results.Ok(new { ok = true, durationMs = ms, serverVersion = version, error = (string?)null });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException or TimeoutException)
        {
            var ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
                $"novarad connection test: failed in {ms}ms", http, ct);
            return Results.Ok(new { ok = false, durationMs = ms, serverVersion = (string?)null, error = ex.Message });
        }
    }
}

public sealed record NovaradConnectionSaveRequest(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    bool UseSsl,
    string? NovaradAuditTable,
    string? Notes);
