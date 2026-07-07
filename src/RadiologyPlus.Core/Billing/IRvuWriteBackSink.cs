namespace RadiologyPlus.Core.Billing;

/// <summary>
/// Pushes our effective CMS/curated work-RVU truth out to the M*Modal/FFI dictation
/// system so its stored RVUs match what reconciliation credits. The real target is
/// <c>[Exam].[ExamCode].[RelativeValueUnit]</c> (SQL Server <c>float</c>, nullable),
/// matched on the active <c>[Exam].[ExamCode]</c> row for a <c>[Code]</c> = HCPCS
/// (i.e. <c>WHERE [Code] = @hcpcs AND [IsDeleted] IS NULL</c>; <c>[Code]</c> is not
/// unique on its own — the unique key is <c>(Code, IssuerKey, IsDeleted)</c>, so a
/// code can exist once per issuer).
/// </summary>
/// <remarks>
/// The write is <em>diff-only</em> (only codes whose RVU actually changed are updated),
/// transactional, and dual-audited into <c>audit.access_logs</c>
/// (<see cref="RadiologyPlus.Core.Audit.AccessAction.MModalWrite"/>). The sink self-gates
/// on a per-tenant <c>tenancy.mmodal_connections</c> row: when none is configured it
/// reports <see cref="RvuSyncPreview.Configured"/> = <c>false</c> and writes nothing, so
/// nothing ever touches a live DB until a connection is configured (mirrors the
/// stub-until-live spirit of <see cref="RadiologyPlus.Core.TechValidation.IFfiComparisonSink"/>).
/// </remarks>
public interface IRvuWriteBackSink
{
    /// <summary>True when an M*Modal connection is configured for the tenant.</summary>
    Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// List the M*Modal issuers (facilities) that have active exam codes, so the operator can
    /// choose which one to sync. The connection's pinned issuer is flagged <see cref="MModalIssuer.IsDefault"/>.
    /// Empty when the write-back isn't configured.
    /// </summary>
    Task<IReadOnlyList<MModalIssuer>> ListIssuersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dry run: read what M*Modal currently stores for the supplied effective figures and
    /// return the per-code diff (current vs new) without writing anything.
    /// <paramref name="issuerKey"/> scopes to one facility; <c>null</c> = ALL issuers.
    /// </summary>
    Task<RvuSyncPreview> PreviewAsync(
        Guid tenantId,
        short year,
        char quarter,
        Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply the diff: transactionally UPDATE only the codes whose RVU changed, then write
    /// a dual-audit row to <c>audit.access_logs</c>. Returns the run result (matched /
    /// updated / unchanged / missing counts). Rolls back the M*Modal write on any failure.
    /// <paramref name="issuerKey"/> scopes to one facility; <c>null</c> = ALL issuers.
    /// </summary>
    Task<RvuSyncResult> ApplyAsync(
        Guid tenantId,
        short year,
        char quarter,
        Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired,
        Guid userId,
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the current <c>[RelativeValueUnit]</c> of every active exam code in scope — a
    /// backup snapshot of M*Modal's present state. <paramref name="issuerKey"/> scopes to one
    /// facility; <c>null</c> = ALL issuers. Blanks are captured as <c>null</c>. Empty when the
    /// write-back isn't configured.
    /// </summary>
    Task<IReadOnlyList<RvuSnapshotRow>> CaptureCurrentRvusAsync(
        Guid tenantId,
        Guid? issuerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restore a snapshot: transactionally write the supplied (issuer, code) → RVU values back
    /// into M*Modal (diff-only; blanks restored verbatim), then dual-audit. Each row is written
    /// to its own issuer, so a restore is inherently scoped to the snapshot's rows. Rolls back
    /// on any failure.
    /// </summary>
    Task<RvuRestoreResult> RestoreRvusAsync(
        Guid tenantId,
        Guid? issuerKey,
        IReadOnlyList<RvuSnapshotRow> rows,
        Guid userId,
        string username,
        CancellationToken cancellationToken = default);
}

/// <summary>One captured exam-code RVU: <c>(issuer, code) → value</c>. <c>Rvu</c> null = blank in M*Modal.</summary>
public sealed record RvuSnapshotRow(Guid IssuerKey, string Code, double? Rvu);

/// <summary>Result of a snapshot restore write into M*Modal.</summary>
public sealed record RvuRestoreResult(
    bool Configured,
    int Restored,
    int Unchanged,
    int Missing,
    bool Success,
    string? Error,
    DateTimeOffset RanAt);

/// <summary>
/// One M*Modal issuer (facility) that owns exam codes. <see cref="ActiveCodeCount"/> is its
/// active (non-deleted) <c>[Exam].[ExamCode]</c> rows; <see cref="IsDefault"/> marks the
/// connection's pinned issuer (the UI preselects it).
/// </summary>
public sealed record MModalIssuer(
    Guid IssuerKey,
    string Name,
    string? Description,
    int ActiveCodeCount,
    bool IsDefault);

/// <summary>One (HCPCS → work RVU) figure bound for the dictation system.</summary>
public sealed record RvuWriteBackEntry(string Hcpcs, decimal WorkRvu);

/// <summary>
/// One code's diff between our effective work RVU and what M*Modal currently stores.
/// <c>Action</c> is <c>"update"</c> (RVU differs — will be written), <c>"unchanged"</c>
/// (already equal — skipped), or <c>"missing"</c> (no active M*Modal row for the code).
/// <c>CurrentRvu</c> is null when the code is missing or M*Modal stores NULL.
/// </summary>
public sealed record RvuSyncDiff(
    string Hcpcs,
    decimal? CurrentRvu,
    decimal NewRvu,
    int MatchedRows,
    string Action);

/// <summary>Result of a <see cref="IRvuWriteBackSink.PreviewAsync"/> dry run.</summary>
public sealed record RvuSyncPreview(
    bool Configured,
    short Year,
    char Quarter,
    int Total,
    int Updatable,
    int Unchanged,
    int Missing,
    IReadOnlyList<RvuSyncDiff> Diffs);

/// <summary>Result of an <see cref="IRvuWriteBackSink.ApplyAsync"/> run.</summary>
public sealed record RvuSyncResult(
    bool Configured,
    short Year,
    char Quarter,
    int Matched,
    int Updated,
    int Unchanged,
    int Missing,
    bool Success,
    string? Error,
    DateTimeOffset RanAt);
