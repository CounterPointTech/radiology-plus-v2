using Microsoft.Extensions.Logging;
using RadiologyPlus.Core.Billing;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Hard-off <see cref="IRvuWriteBackSink"/>: always reports not-configured and writes
/// nothing. The real sink (<see cref="MModalRvuWriteBackSink"/>) already self-gates on a
/// per-tenant <c>tenancy.mmodal_connections</c> row, so this exists only as an explicit
/// "write-back fully disabled" registration (e.g. for tests) — mirrors
/// <see cref="RadiologyPlus.Data.TechValidation.NoOpFfiComparisonSink"/>.
/// </summary>
public sealed class NoOpRvuWriteBackSink : IRvuWriteBackSink
{
    private readonly ILogger<NoOpRvuWriteBackSink> _logger;

    public NoOpRvuWriteBackSink(ILogger<NoOpRvuWriteBackSink> logger) => _logger = logger;

    public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<MModalIssuer>> ListIssuersAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MModalIssuer>>(Array.Empty<MModalIssuer>());

    public Task<RvuSyncPreview> PreviewAsync(
        Guid tenantId, short year, char quarter, Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "NoOpRvuWriteBackSink: write-back disabled — would preview {Count} code(s) for {Year}{Quarter}.",
            desired?.Count ?? 0, year, quarter);
        return Task.FromResult(new RvuSyncPreview(
            Configured: false, year, quarter, Total: 0, Updatable: 0, Unchanged: 0, Missing: 0,
            Diffs: Array.Empty<RvuSyncDiff>()));
    }

    public Task<RvuSyncResult> ApplyAsync(
        Guid tenantId, short year, char quarter, Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired, Guid userId, string username,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "NoOpRvuWriteBackSink: write-back disabled — would push {Count} code(s) for {Year}{Quarter}.",
            desired?.Count ?? 0, year, quarter);
        return Task.FromResult(new RvuSyncResult(
            Configured: false, year, quarter, Matched: 0, Updated: 0, Unchanged: 0, Missing: 0,
            Success: false, Error: "M*Modal write-back is not configured for this tenant.",
            RanAt: DateTimeOffset.Now));
    }
}
