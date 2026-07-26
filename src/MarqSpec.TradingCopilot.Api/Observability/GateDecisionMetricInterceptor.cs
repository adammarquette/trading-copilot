using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// Emits the <c>trading.gate.decisions</c> counter (gh#295, gh#232) by counting every <see cref="GateDecisionRecord"/>
/// as it is written. Counting the rows themselves — rather than threading the metrics sink through every send / arm /
/// take / modify handler — makes the counter <b>track the persisted rows</b>, which is what makes PRD §7's
/// <i>"100% risk-gate coverage"</i> observable rather than merely asserted in tests.
/// </summary>
/// <remarks>
/// <para>
/// Counted in <c>SavingChanges</c>, immediately before the write, so it observes exactly the rows this unit of work
/// is about to persist. (The only writer of these rows, <c>OrderEndpoints.PersistDecision</c>, adds and saves in the
/// same handler pass, so this is effectively the persist moment.) A save that then <b>rolls back</b> — a rare error
/// path — leaves the counter one ahead of the table until the next decision: an acceptable <i>upward</i> drift on an
/// error path, never an under-count that would hide a coverage gap. Exact reconciliation would need a
/// capture-in-<c>SavingChanges</c> / emit-in-<c>SavedChanges</c> pair; the best-effort count is the deliberate trade.
/// </para>
/// <para>
/// Deliberately <b>total</b>: any fault is swallowed, because instrumentation must never fail a trade (engineering
/// §9) — an interceptor that threw here would abort the very <c>SaveChanges</c> it is only meant to observe.
/// Cardinality stays bounded (outcome × binding layer); no account or order id becomes a label.
/// </para>
/// </remarks>
public sealed class GateDecisionMetricInterceptor : SaveChangesInterceptor
{
    private readonly ExecutionMetrics _metrics;
    private readonly ILogger<GateDecisionMetricInterceptor> _logger;

    /// <summary>Creates the interceptor over the execution SLIs sink.</summary>
    /// <param name="metrics">The execution SLIs — the gate-decisions counter is emitted here.</param>
    /// <param name="logger">The logger.</param>
    public GateDecisionMetricInterceptor(ExecutionMetrics metrics, ILogger<GateDecisionMetricInterceptor> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        RecordGateDecisions(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        RecordGateDecisions(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void RecordGateDecisions(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            foreach (EntityEntry<GateDecisionRecord> entry in context.ChangeTracker.Entries<GateDecisionRecord>())
            {
                if (entry.State == EntityState.Added)
                {
                    _metrics.RecordGateDecision(entry.Entity.Outcome, entry.Entity.BindingLayer);
                }
            }
        }
        catch (Exception error)
        {
            // Observing a save must never fail it (engineering §9).
            _logger.LogError(error, "Recording gate-decision metrics failed; the save is unaffected.");
        }
    }
}
