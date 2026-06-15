using Microsoft.Extensions.Logging;
using RadiologyPlus.Core.Billing;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Default <see cref="IRvuWriteBackSink"/>. Logs and returns 0 — the real M*Modal
/// SQL Server write lands here once the dictation DBs are installed (see the
/// <c>TODO(MModal-rvu-writeback)</c> marker on the interface). Mirrors
/// <see cref="RadiologyPlus.Data.TechValidation.NoOpFfiComparisonSink"/>.
/// </summary>
public sealed class NoOpRvuWriteBackSink : IRvuWriteBackSink
{
    private readonly ILogger<NoOpRvuWriteBackSink> _logger;

    public NoOpRvuWriteBackSink(ILogger<NoOpRvuWriteBackSink> logger) => _logger = logger;

    public Task<int> PushWorkRvusAsync(
        Guid tenantId,
        short year,
        char quarter,
        IReadOnlyList<RvuWriteBackEntry> entries,
        CancellationToken cancellationToken = default)
    {
        // TODO(MModal-rvu-writeback): once the dictation DBs are installed, UPDATE
        // [Exam].[ExamCode] SET [RelativeValueUnit] = @work WHERE [Code] = @hcpcs
        // against the M*Modal SQL Server. No-op until then so callers can wire the
        // push without any behavior change.
        _logger.LogInformation(
            "NoOpRvuWriteBackSink: would push {Count} work-RVU value(s) for {Year}{Quarter} to M*Modal.",
            entries?.Count ?? 0, year, quarter);
        return Task.FromResult(0);
    }
}
