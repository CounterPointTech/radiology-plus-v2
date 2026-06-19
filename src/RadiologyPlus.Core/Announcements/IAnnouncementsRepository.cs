namespace RadiologyPlus.Core.Announcements;

public interface IAnnouncementsRepository
{
    /// <summary>
    /// The poll read path: the single banner to show right now for this tenant, honoring
    /// <c>is_active</c>, the schedule window (<c>starts_at</c>/<c>ends_at</c> vs now), and
    /// facility scope (<c>facility_id IS NULL</c> = all facilities; v1 passes null).
    /// Returns the most severe active banner (severity DESC, then most recently updated),
    /// or null when none is active.
    /// </summary>
    Task<StatusBanner?> GetActiveBannerAsync(
        Guid tenantId,
        long? facilityId,
        CancellationToken cancellationToken = default);

    /// <summary>List all banners (active + inactive) for the admin view, newest first.</summary>
    Task<IReadOnlyList<StatusBanner>> ListBannersAsync(
        Guid tenantId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Create a banner. Returns the persisted row.</summary>
    Task<StatusBanner> CreateBannerAsync(
        Guid tenantId,
        Guid createdByUserId,
        StatusBannerCreate input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a banner's content/severity/animation/schedule. Returns the row before and
    /// after the change so the caller can audit the transition. Throws
    /// <see cref="KeyNotFoundException"/> when the banner doesn't exist for the tenant.
    /// </summary>
    Task<StatusBannerUpdateResult> UpdateBannerAsync(
        Guid tenantId,
        long bannerId,
        StatusBannerUpdate input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle a banner's visibility. Returns the updated row. Throws
    /// <see cref="KeyNotFoundException"/> when the banner doesn't exist for the tenant.
    /// </summary>
    Task<StatusBanner> SetBannerActiveAsync(
        Guid tenantId,
        long bannerId,
        bool isActive,
        CancellationToken cancellationToken = default);

    /// <summary>Delete a banner. Returns false when no such banner existed (so the caller can 404).</summary>
    Task<bool> DeleteBannerAsync(
        Guid tenantId,
        long bannerId,
        CancellationToken cancellationToken = default);
}
