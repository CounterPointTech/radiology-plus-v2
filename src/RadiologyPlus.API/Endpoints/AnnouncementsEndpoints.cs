using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RadiologyPlus.Core.Announcements;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.API.Endpoints;

public static class AnnouncementsEndpoints
{
    // Guardrail against a pathological paste; the UI keeps messages short.
    private const int MaxMessageLength = 2000;

    public static IEndpointRouteBuilder MapAnnouncementsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/announcements")
            .WithTags("Announcements")
            .RequireAuthorization();

        // Poll target — any authenticated user (no admin gate); everyone sees the banner.
        group.MapGet("/active", GetActiveBannerAsync).WithName("AnnouncementsGetActive");

        // Admin authoring CRUD (NRS|Admin).
        group.MapGet("/banners", ListBannersAsync).WithName("AnnouncementsListBanners");
        group.MapPost("/banners", CreateBannerAsync).WithName("AnnouncementsCreateBanner");
        group.MapPut("/banners/{bannerId:long}", UpdateBannerAsync).WithName("AnnouncementsUpdateBanner");
        group.MapPatch("/banners/{bannerId:long}/active", SetBannerActiveAsync).WithName("AnnouncementsSetBannerActive");
        group.MapDelete("/banners/{bannerId:long}", DeleteBannerAsync).WithName("AnnouncementsDeleteBanner");

        return app;
    }

    [Authorize]
    private static async Task<IResult> GetActiveBannerAsync(
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        // Every signed-in user sees the active banner. v1 is tenant-wide (facilityId null).
        var banner = await repo.GetActiveBannerAsync(user.TenantId, facilityId: null, ct);
        return Results.Ok(banner);
    }

    [Authorize]
    private static async Task<IResult> ListBannersAsync(
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var rows = await repo.ListBannersAsync(user.TenantId, Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> CreateBannerAsync(
        BannerCreateRequest req,
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var validation = ValidateBanner(req?.Message, req?.Severity, req?.MarqueeSpeed, req?.StartsAt, req?.EndsAt);
        if (validation is not null) return validation;

        var input = new StatusBannerCreate(
            Message:      req!.Message,
            Severity:     req.Severity,
            IsAnimated:   req.IsAnimated,
            IsActive:     req.IsActive,
            MarqueeSpeed: req.MarqueeSpeed,
            StartsAt:     req.StartsAt,
            EndsAt:       req.EndsAt);
        var row = await repo.CreateBannerAsync(user.TenantId, user.UserId, input, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Create,
            $"core.status_banners banner_id={row.BannerId}: created severity={row.Severity} active={row.IsActive}",
            http, ct);

        return Results.Ok(row);
    }

    [Authorize]
    private static async Task<IResult> UpdateBannerAsync(
        long bannerId,
        BannerUpdateRequest req,
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var validation = ValidateBanner(req?.Message, req?.Severity, req?.MarqueeSpeed, req?.StartsAt, req?.EndsAt);
        if (validation is not null) return validation;

        try
        {
            var input = new StatusBannerUpdate(
                Message:      req!.Message,
                Severity:     req.Severity,
                IsAnimated:   req.IsAnimated,
                MarqueeSpeed: req.MarqueeSpeed,
                StartsAt:     req.StartsAt,
                EndsAt:       req.EndsAt);
            var result = await repo.UpdateBannerAsync(user.TenantId, bannerId, input, ct);

            await audit.WriteSuccessAsync(
                user.TenantId, user, AccessAction.Update,
                $"core.status_banners banner_id={bannerId}: " +
                    $"severity {result.Before.Severity}->{result.After.Severity}, message updated",
                http, ct);

            return Results.Ok(result.After);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"Banner {bannerId} not found." });
        }
    }

    [Authorize]
    private static async Task<IResult> SetBannerActiveAsync(
        long bannerId,
        BannerActiveRequest req,
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (req is null) return Results.BadRequest(new { error = "Request body is required." });

        try
        {
            var row = await repo.SetBannerActiveAsync(user.TenantId, bannerId, req.IsActive, ct);

            await audit.WriteSuccessAsync(
                user.TenantId, user, AccessAction.Update,
                $"core.status_banners banner_id={bannerId}: active->{req.IsActive}",
                http, ct);

            return Results.Ok(row);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"Banner {bannerId} not found." });
        }
    }

    [Authorize]
    private static async Task<IResult> DeleteBannerAsync(
        long bannerId,
        ICurrentUser currentUser,
        IAnnouncementsRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var removed = await repo.DeleteBannerAsync(user.TenantId, bannerId, ct);
        if (!removed)
            return Results.NotFound(new { error = $"Banner {bannerId} not found." });

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Delete,
            $"core.status_banners banner_id={bannerId} removed",
            http, ct);

        return Results.NoContent();
    }

    // Shared validation for create + update. A null message/severity means an empty body.
    private static IResult? ValidateBanner(string? message, short? severity, short? marqueeSpeed, DateTimeOffset? startsAt, DateTimeOffset? endsAt)
    {
        if (message is null && severity is null)
            return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(message))
            return Results.BadRequest(new { error = "message is required." });
        if (message.Length > MaxMessageLength)
            return Results.BadRequest(new { error = $"message cannot exceed {MaxMessageLength} characters." });
        if (severity is not (1 or 2 or 3 or 4))
            return Results.BadRequest(new { error = "severity must be 1 (info), 2 (maintenance), 3 (warning), or 4 (critical)." });
        if (marqueeSpeed is < 1 or > 10)
            return Results.BadRequest(new { error = "marqueeSpeed must be between 1 and 10." });
        if (startsAt is not null && endsAt is not null && endsAt <= startsAt)
            return Results.BadRequest(new { error = "The end time must be after the start time." });
        return null;
    }
}

public sealed record BannerCreateRequest(
    string Message,
    short Severity,
    bool IsAnimated,
    bool IsActive,
    short MarqueeSpeed = 5,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record BannerUpdateRequest(
    string Message,
    short Severity,
    bool IsAnimated,
    short MarqueeSpeed = 5,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record BannerActiveRequest(bool IsActive);
