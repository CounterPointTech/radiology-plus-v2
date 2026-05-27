using System.Globalization;
using Microsoft.Extensions.Logging;
#pragma warning disable CA1848 // LoggerMessage delegates are perf-advisory, not reliability.
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.TechValidation;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.TechValidation;

/// <summary>
/// DICOM study merge. Pre-flights against pacs.studies for status/patient/validity
/// equivalence, then performs each losing study's merge in its own dual-audited
/// Novarad transaction via <see cref="INovaradWriter"/>. On a per-study failure
/// we stop processing the remaining losers and report the partial outcome — the
/// already-merged losers stay merged (their transactions committed), and the
/// caller sees exactly which study broke.
/// </summary>
public sealed class StudyMergeService : IStudyMergeService
{
    private readonly INovaradDbContext _novarad;
    private readonly INovaradWriter _writer;
    private readonly INovaradStudyReader _reader;
    private readonly ITechValidationRepository _repo;
    private readonly ITenantContextAccessor _tenants;
    private readonly ILogger<StudyMergeService> _logger;

    public StudyMergeService(
        INovaradDbContext novarad,
        INovaradWriter writer,
        INovaradStudyReader reader,
        ITechValidationRepository repo,
        ITenantContextAccessor tenants,
        ILogger<StudyMergeService> logger)
    {
        _novarad = novarad;
        _writer = writer;
        _reader = reader;
        _repo = repo;
        _tenants = tenants;
        _logger = logger;
    }

    public async Task<StudyMergeOutcome> MergeStudiesAsync(
        StudyMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = _tenants.Require();

        // Pre-flight: load (id, patient, status, is_valid) for the winner and each
        // loser in one query; validate before any write.
        var preflight = await ReadPreflightAsync(request.WinningStudyId, request.LosingStudyIds, cancellationToken);
        var preflightError = ValidatePreflight(request, preflight);
        if (preflightError is not null)
        {
            _logger.LogWarning(
                "Study merge pre-flight rejected for tenant {Tenant}: {Reason}",
                tenant.TenantId, preflightError);
            return new StudyMergeOutcome(
                request.WinningStudyId, 0, request.LosingStudyIds.Count,
                Array.Empty<StudyMergeRowResult>(), preflightError);
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Merge into keeper study {request.WinningStudyId}"
            : request.Reason!;

        var results = new List<StudyMergeRowResult>(request.LosingStudyIds.Count);
        var refreshIds = new List<long>(request.LosingStudyIds.Count + 1) { request.WinningStudyId };

        foreach (var losingId in request.LosingStudyIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                int seriesRePointed = 0;
                await _writer.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        var npg = (NpgsqlConnection)conn;

                        await using (var movSeries = npg.CreateCommand())
                        {
                            movSeries.Transaction = (NpgsqlTransaction)tx;
                            movSeries.CommandText = """
                                UPDATE pacs.series
                                SET study = @winning, modified_date = LOCALTIMESTAMP
                                WHERE study = @losing
                                """;
                            movSeries.Parameters.AddWithValue("winning", request.WinningStudyId);
                            movSeries.Parameters.AddWithValue("losing", losingId);
                            seriesRePointed = await movSeries.ExecuteNonQueryAsync(c);
                        }

                        await using (var softDelete = npg.CreateCommand())
                        {
                            softDelete.Transaction = (NpgsqlTransaction)tx;
                            softDelete.CommandText = """
                                UPDATE pacs.studies
                                SET is_valid = FALSE, modified_date = LOCALTIMESTAMP
                                WHERE id = @losing
                                """;
                            softDelete.Parameters.AddWithValue("losing", losingId);
                            await softDelete.ExecuteNonQueryAsync(c);
                        }

                        return new
                        {
                            winningStudyId = request.WinningStudyId,
                            losingStudyId = losingId,
                            seriesRePointed,
                        };
                    },
                    description: "PACS: merge study into keeper",
                    reason: reason,
                    resourceType: "pacs.studies",
                    resourceId: losingId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: cancellationToken);

                results.Add(new StudyMergeRowResult(losingId, Success: true, seriesRePointed, ErrorMessage: null));
                refreshIds.Add(losingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Study merge failed for tenant {Tenant} losing={Losing} into keeper={Keeper}; aborting remaining merges.",
                    tenant.TenantId, losingId, request.WinningStudyId);
                results.Add(new StudyMergeRowResult(losingId, Success: false, SeriesRePointed: 0, ErrorMessage: ex.Message));
                // Stop processing further losers — operator triages the partial outcome.
                break;
            }
        }

        // Refresh the worklist projection so losers drop off and the keeper's row
        // reflects its new state. Best-effort: writes already committed.
        try
        {
            var fresh = new List<ReadyStudy>(refreshIds.Count);
            foreach (var id in refreshIds)
            {
                var row = await _reader.ReadStudyByIdAsync(id, cancellationToken);
                if (row is not null) fresh.Add(row);
            }
            if (fresh.Count > 0)
                await _repo.UpsertReadyStudiesAsync(tenant.TenantId, fresh, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-merge projection refresh failed for tenant {Tenant}; worklist will self-heal on the next projector pass.",
                tenant.TenantId);
        }

        var merged = results.Count(r => r.Success);
        var failed = request.LosingStudyIds.Count - merged;
        return new StudyMergeOutcome(
            request.WinningStudyId, merged, failed, results, PreflightError: null);
    }

    private async Task<IReadOnlyDictionary<long, PreflightRow>> ReadPreflightAsync(
        long winningId, IReadOnlyList<long> losingIds, CancellationToken ct)
    {
        var ids = new List<long>(losingIds.Count + 1) { winningId };
        ids.AddRange(losingIds);

        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, patient, status, is_valid
            FROM pacs.studies
            WHERE id = ANY(@ids)
            """;
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = ids.ToArray(),
        });

        var map = new Dictionary<long, PreflightRow>(ids.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetInt64(0)] = new PreflightRow(
                Id: reader.GetInt64(0),
                Patient: reader.GetInt64(1),
                Status: reader.GetInt32(2),
                IsValid: reader.GetBoolean(3));
        }
        return map;
    }

    private static string? ValidatePreflight(
        StudyMergeRequest req, IReadOnlyDictionary<long, PreflightRow> rows)
    {
        if (req.LosingStudyIds.Count == 0)
            return "At least one losing study is required.";

        var distinctLosers = new HashSet<long>(req.LosingStudyIds);
        if (distinctLosers.Count != req.LosingStudyIds.Count)
            return "Losing study ids must be unique.";
        if (distinctLosers.Contains(req.WinningStudyId))
            return "The keeper study cannot also be a losing study.";

        if (!rows.TryGetValue(req.WinningStudyId, out var keeper))
            return $"Keeper study {req.WinningStudyId} was not found in pacs.studies.";
        if (keeper.Status != 0)
            return $"Keeper study {req.WinningStudyId} is not in NEW status (status={keeper.Status}).";

        foreach (var losingId in req.LosingStudyIds)
        {
            if (!rows.TryGetValue(losingId, out var loser))
                return $"Losing study {losingId} was not found in pacs.studies.";
            if (loser.Status != 0)
                return $"Losing study {losingId} is not in NEW status (status={loser.Status}).";
            if (loser.Patient != keeper.Patient)
                return $"Losing study {losingId} belongs to a different patient ({loser.Patient}) than the keeper ({keeper.Patient}).";
        }

        // NOTE: We intentionally do NOT require is_valid=TRUE. The worklist
        // surfaces is_valid=FALSE studies because Do-the-Do flips it later;
        // merging an already-soft-deleted loser still re-points its series.
        return null;
    }

    private sealed record PreflightRow(long Id, long Patient, int Status, bool IsValid);
}
