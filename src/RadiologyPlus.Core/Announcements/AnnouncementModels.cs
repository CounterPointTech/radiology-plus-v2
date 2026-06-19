namespace RadiologyPlus.Core.Announcements;

/// <summary>
/// One admin-authored status banner shown at the top of the app to every signed-in
/// user. <c>Severity</c> is 1=info, 2=maintenance, 3=warning, 4=critical (the UI maps
/// each to a color + icon). <c>FacilityId</c> null = all facilities (per-facility
/// targeting is not yet surfaced; v1 is always tenant-wide). <c>StartsAt</c>/<c>EndsAt</c>
/// bound an optional schedule window. <c>IsDismissible</c> is reserved for a later
/// per-user dismissal extra (v1 banners are sticky).
/// </summary>
public sealed record StatusBanner(
    long BannerId,
    Guid TenantId,
    string Message,
    short Severity,
    bool IsAnimated,
    short MarqueeSpeed,
    bool IsActive,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    long? FacilityId,
    bool IsDismissible,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Write-shape for creating a banner. <c>MarqueeSpeed</c> (1-10) drives the scroll speed
/// when animated. Scheduling (<c>StartsAt</c>/<c>EndsAt</c>) is optional.
/// </summary>
public sealed record StatusBannerCreate(
    string Message,
    short Severity,
    bool IsAnimated,
    bool IsActive,
    short MarqueeSpeed = 5,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

/// <summary>
/// Write-shape for updating a banner's content/severity/animation/speed/schedule. Visibility
/// (<c>is_active</c>) is toggled separately via <see cref="IAnnouncementsRepository.SetBannerActiveAsync"/>.
/// </summary>
public sealed record StatusBannerUpdate(
    string Message,
    short Severity,
    bool IsAnimated,
    short MarqueeSpeed = 5,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

/// <summary>Before/after pair returned from <c>UpdateBannerAsync</c> so the caller can audit the change.</summary>
public sealed record StatusBannerUpdateResult(StatusBanner Before, StatusBanner After);
