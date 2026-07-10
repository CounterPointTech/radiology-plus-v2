using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Facilities console surface (NRS/Admin, enforced per-handler; mutations audited).
/// tenancy.facilities maps the tenant's Novarad facilities into Radiology Plus —
/// federated logins and user-facility links resolve through it. The import pulls
/// the live list from the tenant's Novarad DB.
/// </summary>
public static class FacilitiesEndpoints
{
    public static IEndpointRouteBuilder MapFacilitiesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/facilities")
            .WithTags("Facilities")
            .RequireAuthorization();

        group.MapGet("/", ListAsync).WithName("FacilitiesList");
        group.MapPost("/", CreateAsync).WithName("FacilitiesCreate");
        group.MapPost("/import", ImportAsync).WithName("FacilitiesImport");
        group.MapPut("/{facilityId:int}", UpdateAsync).WithName("FacilitiesUpdate");
        group.MapDelete("/{facilityId:int}", DeleteAsync).WithName("FacilitiesDelete");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUser currentUser, IFacilityAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        return Results.Ok(await repo.ListAsync(user.TenantId, ct));
    }

    private static async Task<IResult> CreateAsync(
        FacilitySaveRequest req,
        ICurrentUser currentUser,
        IFacilityAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (Validate(req) is { } bad) return bad;

        try
        {
            var created = await repo.CreateAsync(user.TenantId, ToSave(req), ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
                $"tenancy.facilities facility_id={created.FacilityId}: created '{created.Code}' (novarad {created.NovaradFacilityId})", http, ct);
            return Results.Ok(created);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"Novarad facility {req.NovaradFacilityId} is already mapped." });
        }
    }

    private static async Task<IResult> UpdateAsync(
        int facilityId,
        FacilitySaveRequest req,
        ICurrentUser currentUser,
        IFacilityAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (Validate(req) is { } bad) return bad;

        try
        {
            var updated = await repo.UpdateAsync(user.TenantId, facilityId, ToSave(req), ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"tenancy.facilities facility_id={facilityId}: updated '{updated.Code}'", http, ct);
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Facility not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"Novarad facility {req.NovaradFacilityId} is already mapped." });
        }
    }

    private static async Task<IResult> DeleteAsync(
        int facilityId,
        ICurrentUser currentUser,
        IFacilityAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        try
        {
            var removed = await repo.DeleteAsync(user.TenantId, facilityId, ct);
            if (!removed) return Results.NotFound(new { error = "Facility not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Conflict(new { error = "Users are assigned to this facility — deactivate it instead of deleting." });
        }

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            $"tenancy.facilities facility_id={facilityId} removed", http, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ImportAsync(
        ICurrentUser currentUser,
        INovaradFacilityReader novarad,
        IFacilityAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        IReadOnlyList<NovaradFacilityRow> rows;
        try
        {
            rows = await novarad.ListFacilitiesAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException)
        {
            return Results.BadRequest(new { error = $"Couldn't read the Novarad facility list: {ex.Message}" });
        }

        var result = await repo.UpsertFromNovaradAsync(user.TenantId, rows, ct);
        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
            $"tenancy.facilities: imported from Novarad ({result.Inserted} new, {result.Updated} updated of {result.Total})", http, ct);
        return Results.Ok(result);
    }

    private static IResult? Validate(FacilitySaveRequest req)
    {
        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.BadRequest(new { error = "code is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.BadRequest(new { error = "displayName is required." });
        return null;
    }

    private static FacilitySave ToSave(FacilitySaveRequest req) =>
        new(req.NovaradFacilityId, req.Code.Trim(), req.DisplayName.Trim(), req.IsActive);
}

public sealed record FacilitySaveRequest(
    int NovaradFacilityId,
    string Code,
    string DisplayName,
    bool IsActive);
