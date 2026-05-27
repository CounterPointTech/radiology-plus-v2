using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RadiologyPlus.Core.TechValidation;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Service.TechValidation;

public sealed class ReadyStudiesProjectorOptions
{
    /// <summary>How often to re-project, per tenant. Default: 60s.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How far back to look at last_image_processed_date. Default: 7 days.</summary>
    public TimeSpan LookbackWindow { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Prune ready_studies rows whose projected_at is older than this AND no open validation. Default: 4 hours.</summary>
    public TimeSpan PruneAge { get; init; } = TimeSpan.FromHours(4);
}

/// <summary>
/// Per-tenant polling worker. For each active tenant it (a) reads "Ready" studies from
/// the tenant's Novarad, (b) upserts them into <c>tech_validation.ready_studies</c>,
/// (c) prunes stale rows. SignalR broadcast happens inside the projector after each pass
/// so connected worklist clients can refresh without a long-poll.
/// </summary>
public sealed class ReadyStudiesProjector : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReadyStudiesProjectorOptions _options;
    private readonly ILogger<ReadyStudiesProjector> _logger;

    public ReadyStudiesProjector(
        IServiceScopeFactory scopes,
        IOptions<ReadyStudiesProjectorOptions> options,
        ILogger<ReadyStudiesProjector> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ReadyStudiesProjector starting. Poll={Poll}s Lookback={LookbackHours}h Prune={PruneMin}m",
            _options.PollInterval.TotalSeconds, _options.LookbackWindow.TotalHours, _options.PruneAge.TotalMinutes);

        // Stagger the first tick so we don't dog-pile against Novarad on startup.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnePassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadyStudiesProjector pass failed; backing off until next tick.");
            }

            try { await Task.Delay(_options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnePassAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenants = sp.GetRequiredService<ITenantRepository>();
        var tenantAccessor = sp.GetRequiredService<ITenantContextAccessor>();

        var activeTenants = await tenants.ListActiveAsync(ct);
        _logger.LogDebug("ReadyStudiesProjector pass over {Count} tenant(s).", activeTenants.Count);

        foreach (var t in activeTenants)
        {
            ct.ThrowIfCancellationRequested();
            await using var tenantScope = _scopes.CreateAsyncScope();
            var tsp = tenantScope.ServiceProvider;
            var taccessor = tsp.GetRequiredService<ITenantContextAccessor>();
            using var _scopeGuard = taccessor.Push(new TenantContext(t.TenantId, t.Code, t.DisplayName));

            try
            {
                var reader = tsp.GetRequiredService<INovaradStudyReader>();
                var repo = tsp.GetRequiredService<ITechValidationRepository>();

                var studies = await reader.ReadReadyStudiesAsync(_options.LookbackWindow, ct);
                if (studies.Count > 0)
                {
                    await repo.UpsertReadyStudiesAsync(t.TenantId, studies, ct);
                }

                await repo.PruneStaleReadyStudiesAsync(t.TenantId, DateTimeOffset.UtcNow.Subtract(_options.PruneAge), ct);

                _logger.LogInformation(
                    "ReadyStudiesProjector projected {Count} ready studies for tenant {Tenant}.",
                    studies.Count, t.Code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadyStudiesProjector failed for tenant {Tenant}; continuing with next tenant.", t.Code);
            }
        }
    }
}
