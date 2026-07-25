using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The startup decision-state rehydration pass (gh#221, ADR-0013): on boot, read the whole decision surface back
/// and make its return <b>visible</b> and <b>inert</b> — nothing here resumes an order, fires a conditional, or
/// promotes a stop. If a crash mid-write left an <b>impossible combination</b>, fail <b>safe and loud</b>: engage
/// the kill switch (<see cref="KillSwitchMode.HaltOnly"/> — no new outbound orders, positions left on their native
/// safety stops) and alert; never silently repair.
/// </summary>
/// <remarks>
/// <para>
/// Background plumbing with <b>no request user</b>, so it reads across all owners with <c>IgnoreQueryFilters</c>
/// (the <see cref="OrphanGuardService"/> precedent) — ownership is carried on every snapshot, never dropped, and a
/// stop plan whose owner has drifted from its order's is itself flagged (R-20). The safety decision is the pure
/// <see cref="DecisionStateRehydration.Analyze"/>; this type only feeds it from EF rows and acts on the verdict.
/// </para>
/// <para>
/// The fail-safe reuses the kill switch — the proven "no new orders" primitive the enforcing send path already
/// reads — rather than inventing a parallel halt. It engages the <b>runtime flag</b> (venue-independent, so it
/// holds even if the venue is unreachable at boot) and persists the durable lock so a recurring bad state stays
/// blocked and the operator sees an engaged switch with the reason. It does <b>not</b> downgrade or overwrite an
/// existing operator lock. The loud alert is a high-severity log carrying <c>synthetic_risk</c> until the Phase-4
/// operator channel and the formal <c>AuditRecord</c> land (the gh#209 interim).
/// </para>
/// </remarks>
public sealed class DecisionStateRehydrator
{
    private readonly TradingCopilotDbContext _database;
    private readonly KillSwitch _killSwitch;
    private readonly ILogger<DecisionStateRehydrator> _logger;

    /// <summary>Creates the rehydrator over the startup-scoped database and the process-wide kill switch.</summary>
    /// <param name="database">The database — read across all owners for discovery (R-20 filter bypassed).</param>
    /// <param name="killSwitch">The process-wide kill switch, engaged as the fail-safe on an inconsistency.</param>
    /// <param name="logger">The logger — the interim operator-alert channel.</param>
    public DecisionStateRehydrator(
        TradingCopilotDbContext database, KillSwitch killSwitch, ILogger<DecisionStateRehydrator> logger)
    {
        _database = database;
        _killSwitch = killSwitch;
        _logger = logger;
    }

    /// <summary>
    /// Rehydrates the decision surface: observes what returned (inert), and on any impossible combination fails safe
    /// to no-new-orders and loud.
    /// </summary>
    /// <param name="now">The startup instant — stamps the kill-switch lock if one is engaged.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The surface report — the counts and any inconsistencies.</returns>
    public async Task<DecisionSurfaceReport> RehydrateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Read the whole surface across every owner -- background plumbing, so the R-20 default-deny filter is
        // bypassed for discovery; ownership is carried on each snapshot (never dropped) and checked below.
        List<RehydratedOrder> orders = await _database.Orders
            .IgnoreQueryFilters()
            .Select(order => new RehydratedOrder(order.Id, order.UserId, order.Status, order.VenueOrderKey != null))
            .ToListAsync(cancellationToken);

        List<RehydratedConditional> conditionals = await _database.ConditionalOrders
            .IgnoreQueryFilters()
            .Select(order => new RehydratedConditional(order.Id, order.UserId, order.Status, order.FiredOrderId))
            .ToListAsync(cancellationToken);

        List<RehydratedStopPlan> stopPlans = await _database.StopPlans
            .IgnoreQueryFilters()
            .Select(plan => new RehydratedStopPlan(plan.Id, plan.UserId, plan.OrderId, plan.Staging))
            .ToListAsync(cancellationToken);

        int activeSuggestions = await _database.Suggestions
            .IgnoreQueryFilters()
            .CountAsync(suggestion => suggestion.State == SuggestionState.Active, cancellationToken);

        DecisionSurfaceReport report =
            DecisionStateRehydration.Analyze(orders, conditionals, stopPlans, activeSuggestions);

        // The surface is back and INERT: nothing here resumes it. A staged order sends only when the operator takes
        // it (re-gated, R-12); a pending conditional fires only on a genuine trigger (re-gated); a hidden stop
        // promotes only as price nears it; a suggestion is data until taken. We observe, never resume.
        _logger.LogInformation(
            "Decision surface rehydrated: {Orders} order(s) ({Staged} staged), {Conditionals} conditional(s) "
            + "({Pending} pending), {Hidden} hidden + {Native} native + {Orphaned} orphaned stop(s), {Suggestions} "
            + "active suggestion(s) — inert; nothing resumed, re-validated on take/fire (R-12).",
            report.Orders, report.StagedOrders, report.Conditionals, report.PendingConditionals,
            report.HiddenStops, report.NativeStops, report.OrphanedStops, report.ActiveSuggestions);

        if (!report.IsConsistent)
        {
            await FailSafeAsync(report, now, cancellationToken);
        }

        return report;
    }

    private async Task FailSafeAsync(
        DecisionSurfaceReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Loud and specific, per inconsistency -- never silently repaired. synthetic_risk is the marker the formal
        // audit will carry (the gh#209 interim alert channel).
        foreach (DecisionInconsistency issue in report.Inconsistencies)
        {
            _logger.LogCritical(
                "Rehydration found an impossible decision-state combination ({Kind}, synthetic_risk): {Detail} "
                + "[owner {Owner}, entity {Entity}].",
                issue.Kind, issue.Detail, issue.Owner, issue.EntityId);
        }

        if (_killSwitch.IsEngaged)
        {
            // Already locked (the operator's own engage, rehydrated just before this pass) -- outbound is already
            // refused. Do not downgrade the mode or overwrite the reason; the loud alerts above stand.
            _logger.LogCritical(
                "{Count} impossible decision-state combination(s) found at startup; the kill switch is already "
                + "engaged, so outbound orders remain blocked. Investigate before disengaging.",
                report.Inconsistencies.Count);
            return;
        }

        string reason = LockReason(report);
        _killSwitch.Engage(KillSwitchMode.HaltOnly, now, reason);
        await PersistLockAsync(now, reason, cancellationToken);

        _logger.LogCritical(
            "Kill switch ENGAGED (HaltOnly) by rehydration: {Count} impossible decision-state combination(s) at "
            + "startup. Outbound orders are blocked; open positions rest on their native safety stops. Investigate, "
            + "then disengage.",
            report.Inconsistencies.Count);
    }

    private static string LockReason(DecisionSurfaceReport report) =>
        $"Startup rehydration found {report.Inconsistencies.Count} impossible decision-state combination(s): "
        + string.Join("; ", report.Inconsistencies.Select(issue => issue.Kind).Distinct());

    private async Task PersistLockAsync(DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        // Persist the durable lock so it SURVIVES this restart too (a bad state that recurs stays blocked) and the
        // operator sees an engaged switch with the reason. KillSwitchState is deployment plumbing, not R-20-scoped.
        KillSwitchState? row = await _database.KillSwitchStates.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new KillSwitchState { Id = KillSwitchState.SingletonId };
            _database.KillSwitchStates.Add(row);
        }

        row.Engaged = true;
        row.Mode = KillSwitchMode.HaltOnly;
        row.EngagedAt = now;
        row.Reason = reason;
        row.UpdatedAt = now;
        await _database.SaveChangesAsync(cancellationToken);
    }
}
