namespace RadiologyPlus.Core.Billing;

/// <summary>
/// Pushes the current CMS work-RVU truth (<c>billing.rvu_values</c>) out to the
/// M*Modal/FFI dictation system so its stored RVUs match what reconciliation credits.
/// The real target is <c>[Exam].[ExamCode].[RelativeValueUnit]</c> (SQL Server float,
/// nullable), matched on <c>[Exam].[ExamCode].[Code]</c> = HCPCS.
/// </summary>
/// <remarks>
/// TODO(MModal-rvu-writeback): implement the real SQL Server write once the M*Modal
/// DBs are installed (schema at <c>References/DB Schemas/MModal</c>, gitignored). Until
/// then <see cref="NoOpRvuWriteBackSink"/> logs and returns — mirrors
/// <see cref="RadiologyPlus.Core.TechValidation.IFfiComparisonSink"/>. Registration
/// relocates to <c>RadiologyPlus.AdminService</c> during the Part 2 technical/clinical
/// split; until then it lives in the existing API/Service DI.
/// </remarks>
public interface IRvuWriteBackSink
{
    /// <summary>
    /// Push the supplied (HCPCS → work RVU) figures from a given snapshot to the
    /// dictation system. Returns the number of rows the sink would update.
    /// </summary>
    Task<int> PushWorkRvusAsync(
        Guid tenantId,
        short year,
        char quarter,
        IReadOnlyList<RvuWriteBackEntry> entries,
        CancellationToken cancellationToken = default);
}

/// <summary>One (HCPCS → work RVU) figure bound for the dictation system.</summary>
public sealed record RvuWriteBackEntry(string Hcpcs, decimal WorkRvu);
