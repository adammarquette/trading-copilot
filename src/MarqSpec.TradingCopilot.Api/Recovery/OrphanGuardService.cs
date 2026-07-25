using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The orphan guard (ADR-0007, ADR-0013, gh#209): when the venue connection drops, a <b>hidden</b> working stop
/// can no longer be promoted, so it is marked <see cref="StopStaging.Orphaned"/> and the operator is warned; on
/// reconnect it is re-armed to <see cref="StopStaging.Hidden"/> so promotion resumes. The native safety stop
/// remains the physical floor throughout — this concerns only the operator's <i>tighter</i> synthetic protection.
/// </summary>
/// <remarks>
/// Background plumbing with <b>no authenticated user</b>, so its queries <c>IgnoreQueryFilters</c> the R-20
/// default-deny filter — it acts for the deployment, over whoever owns the stops; ownership is preserved on write
/// (the same discipline as the stop-promotion watcher). The alert is a high-severity <b>log</b> carrying the
/// <c>synthetic_risk</c> today; the formal <c>AuditRecord</c> and real-time operator alert are deferred (gh#209).
/// </remarks>
public sealed class OrphanGuardService
{
    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<OrphanGuardService> _logger;

    /// <summary>Creates the guard over the scoped database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="logger">The logger.</param>
    public OrphanGuardService(TradingCopilotDbContext database, ILogger<OrphanGuardService> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Orphans every hidden working stop on a venue-connection loss — they can no longer promote, so the
    /// operator's tighter protection is degraded to the native safety stop until reconnect.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were orphaned.</returns>
    public async Task<int> OrphanAsync(CancellationToken cancellationToken)
    {
        List<StopPlanRecord> hidden = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Hidden)
            .ToListAsync(cancellationToken);

        foreach (StopPlanRecord plan in hidden)
        {
            plan.Staging = StopStaging.Orphaned;
        }

        if (hidden.Count > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);

            // An EMERGENCY, not a warning: a live position's tighter protection just degraded. High-severity so
            // it surfaces as the operator alert until the real-time channel (Phase-4 SPA) and the formal
            // AuditRecord land. The synthetic_risk marker is the flag the audit will carry.
            _logger.LogError(
                "Venue connection lost — {Count} hidden stop(s) ORPHANED (synthetic_risk). The native safety "
                + "stop remains the floor; the tighter working stop re-arms on reconnect.",
                hidden.Count);
        }

        return hidden.Count;
    }

    /// <summary>
    /// Re-arms every orphaned stop on reconnect — back to <see cref="StopStaging.Hidden"/> so the promotion
    /// watcher resumes. (Full per-position re-validation against venue truth is a deferred refinement, gh#209; a
    /// promotion for a since-closed position self-rejects at the venue, and the safety stop was the floor.)
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were re-armed.</returns>
    public async Task<int> RearmAsync(CancellationToken cancellationToken)
    {
        List<StopPlanRecord> orphaned = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Orphaned)
            .ToListAsync(cancellationToken);

        foreach (StopPlanRecord plan in orphaned)
        {
            plan.Staging = StopStaging.Hidden;
        }

        if (orphaned.Count > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Venue connection restored — {Count} orphaned stop(s) RE-ARMED to hidden; promotion resumes.",
                orphaned.Count);
        }

        return orphaned.Count;
    }
}
