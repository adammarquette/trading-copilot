using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// Isolates every execution-SLI measurement from the trading action being measured (gh#343, engineering §9):
/// <i>"emitting a metric can never fail a trading action: a metrics failure is logged and the action completes."</i>
/// A fault in the sink is absorbed and logged at <b>error</b>; the caller never sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A decorator rather than a <c>try</c>/<c>catch</c> at each call site.</b> <see cref="IExecutionMetrics"/> is
/// injected into six services and measured from more places than that; guarding each one leaves the invariant to be
/// re-remembered by whoever adds the next measurement. Wrapping the sink enforces it in <b>one</b> place, for every
/// current and future call site, and cannot be forgotten.
/// </para>
/// <para>
/// <b>What this prevents</b> — found by QA gh#331, each observed rather than imagined: a throwing sink failed a send
/// <i>after</i> the venue had accepted the order and <i>before</i> the journal was written, leaving a <b>live order
/// with no <c>Order</c> row and no stop plan</b> while the operator was told the send had failed; and it failed the
/// operator's <b>emergency halt</b> even though the kill switch had in fact engaged. Instrumentation became the
/// outage it exists to reveal.
/// </para>
/// <para>
/// <b>Every fault is absorbed, including <see cref="OperationCanceledException"/>.</b> The seam takes no
/// cancellation token, so a cancellation surfacing from a sink is not a cooperative stop of the trading action — it
/// is a sink fault like any other, and propagating it would be exactly the failure this class exists to prevent.
/// (Elsewhere in the codebase a caller's token <i>is</i> available and its cancellation is deliberately rethrown;
/// that distinction is why this one differs.)
/// </para>
/// <para>
/// The shipped <see cref="ExecutionMetrics"/> is total by construction, so this guards the <b>seam</b> rather than a
/// present-day bug: <see cref="IExecutionMetrics"/> is a Domain-declared interface, so any exporter-backed or
/// further-decorating sink registered later is where the fault would come from.
/// </para>
/// </remarks>
public sealed class FailureTolerantExecutionMetrics : IExecutionMetrics
{
    private readonly IExecutionMetrics _inner;
    private readonly ILogger<FailureTolerantExecutionMetrics> _logger;

    /// <summary>Creates the decorator over the real sink.</summary>
    /// <param name="inner">The sink that actually records.</param>
    /// <param name="logger">The logger each absorbed fault is surfaced on.</param>
    public FailureTolerantExecutionMetrics(IExecutionMetrics inner, ILogger<FailureTolerantExecutionMetrics> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc />
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer) =>
        Safely(() => _inner.RecordGateDecision(outcome, bindingLayer), nameof(RecordGateDecision));

    /// <inheritdoc />
    public void RecordFlattenDeadline(FlattenTier tier, string outcome) =>
        Safely(() => _inner.RecordFlattenDeadline(tier, outcome), nameof(RecordFlattenDeadline));

    /// <inheritdoc />
    public void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed) =>
        Safely(() => _inner.RecordTimeToFlat(tier, elapsed), nameof(RecordTimeToFlat));

    /// <inheritdoc />
    public void RecordOrderAck(TimeSpan elapsed) =>
        Safely(() => _inner.RecordOrderAck(elapsed), nameof(RecordOrderAck));

    /// <inheritdoc />
    public void SetKillSwitchEngaged(bool engaged) =>
        Safely(() => _inner.SetKillSwitchEngaged(engaged), nameof(SetKillSwitchEngaged));

    /// <inheritdoc />
    public void SetOrphanedStops(int count) =>
        Safely(() => _inner.SetOrphanedStops(count), nameof(SetOrphanedStops));

    /// <inheritdoc />
    public void SetPositionProtection(int open, int unprotected) =>
        Safely(() => _inner.SetPositionProtection(open, unprotected), nameof(SetPositionProtection));

    /// <inheritdoc />
    public void SetVenueConnected(bool connected) =>
        Safely(() => _inner.SetVenueConnected(connected), nameof(SetVenueConnected));

    /// <inheritdoc />
    public void RecordRetentionGap(string consumerGroup) =>
        Safely(() => _inner.RecordRetentionGap(consumerGroup), nameof(RecordRetentionGap));

    /// <inheritdoc />
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag) =>
        Safely(() => _inner.RecordPipelineLag(consumerGroup, lag), nameof(RecordPipelineLag));

    /// <inheritdoc />
    public void RecordBackfillShortfall(string contractKey, TimeSpan uncovered) =>
        Safely(() => _inner.RecordBackfillShortfall(contractKey, uncovered), nameof(RecordBackfillShortfall));

    // Absorbed, never rethrown -- but never silent either: a sink that has been failing for a week must be
    // discoverable, so each fault is logged at error with the measurement that was lost.
    private void Safely(Action record, string measurement)
    {
        try
        {
            record();
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Recording the {Measurement} execution SLI failed; the measurement is lost and the trading action is unaffected.",
                measurement);
        }
    }
}
