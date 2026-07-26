using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>Which auto-flatten tier produced a signal (gh#232) — the two must never be merged.</summary>
public enum FlattenTier
{
    /// <summary>The primary scheduler (gh#185).</summary>
    Primary,

    /// <summary>The independent redundant watchdog (gh#187).</summary>
    Watchdog,
}

/// <summary>
/// The execution-specific SLIs (gh#232, ADR-0002, engineering §7) — the signals particular to a system that
/// places orders and flattens before a close, as distinct from the generic RED health gh#230 provides.
/// </summary>
/// <remarks>
/// <para>
/// <b>An absence must be detectable.</b> A counter that merely stays at zero is indistinguishable from a healthy
/// quiet day, so a deadline emits <see cref="RecordFlattenDeadline"/> <i>every time it passes</i> and carries the
/// outcome as a dimension — including "there was nothing to flatten". Without that, "the flatten never fired" and
/// "there was nothing to do" are the same silence, and the failure this system exists to prevent looks exactly
/// like an ordinary Tuesday.
/// </para>
/// <para>
/// <b>Dimensions are a closed set.</b> No account id, order id, or instrument goes in a label. An unbounded label
/// set takes the metrics backend down — instrumentation causing the very outage it exists to reveal (§9).
/// </para>
/// <para>
/// Recording is deliberately total: nothing here throws, so a metrics fault can never fail a trading action.
/// </para>
/// </remarks>
public sealed class ExecutionMetrics : IDisposable
{
    /// <summary>The meter name — registered with the OpenTelemetry pipeline (gh#230).</summary>
    public const string MeterName = "MarqSpec.TradingCopilot.Execution";

    /// <summary>Gate decisions, dimensioned by outcome and binding layer.</summary>
    public const string GateDecisions = "trading.gate.decisions";

    /// <summary>Auto-flatten deadlines evaluated, dimensioned by tier and outcome — emitted even when idle.</summary>
    public const string FlattenDeadlines = "trading.flatten.deadlines";

    /// <summary>Time from deadline to confirmed flat.</summary>
    public const string TimeToFlat = "trading.flatten.time_to_flat";

    /// <summary>Transmit → venue acknowledgement latency.</summary>
    public const string OrderAckLatency = "trading.order.ack_latency";

    /// <summary>Whether the kill switch is engaged (1/0).</summary>
    public const string KillSwitchEngaged = "trading.killswitch.engaged";

    /// <summary>How many working stops are currently orphaned — live synthetic-risk exposure.</summary>
    public const string OrphanedStops = "trading.stops.orphaned";

    /// <summary>Retention gaps observed on read, per consumer group (gh#227).</summary>
    public const string RetentionGaps = "trading.eventlog.retention_gaps";

    /// <summary>Append → consume delay, per consumer group.</summary>
    public const string PipelineLag = "trading.eventlog.pipeline_lag";

    /// <summary>Outcome tag: the flatten closed the position.</summary>
    public const string FlattenExecuted = "executed";

    /// <summary>Outcome tag: the deadline passed with no exposure — the quiet day, recorded rather than inferred.</summary>
    public const string FlattenNothingToDo = "nothing-to-do";

    /// <summary>Outcome tag: the flatten ran out of attempts and escalated.</summary>
    public const string FlattenEscalated = "escalated";

    /// <summary>Outcome tag: the deadline passed with exposure still open, past the firing window.</summary>
    public const string FlattenMissed = "missed";

    /// <summary>Outcome tag: the market is deliberately disabled (R-13's warned override).</summary>
    public const string FlattenDisabled = "disabled";

    private readonly Meter _meter;
    private readonly Counter<long> _gateDecisions;
    private readonly Counter<long> _flattenDeadlines;
    private readonly Histogram<double> _timeToFlat;
    private readonly Histogram<double> _orderAck;
    private readonly Counter<long> _retentionGaps;
    private readonly Histogram<double> _pipelineLag;

    private int _killSwitchEngaged;
    private int _orphanedStops;

    /// <summary>Creates the meter and its instruments.</summary>
    public ExecutionMetrics()
    {
        _meter = new Meter(MeterName);

        _gateDecisions = _meter.CreateCounter<long>(
            GateDecisions, unit: "{decision}", description: "Risk-gate decisions by outcome and binding layer.");

        _flattenDeadlines = _meter.CreateCounter<long>(
            FlattenDeadlines, unit: "{deadline}", description: "Auto-flatten deadlines evaluated, by tier and outcome.");

        // Histograms, never means: p95/p99 are the interesting part on a path racing a market close.
        _timeToFlat = _meter.CreateHistogram<double>(
            TimeToFlat, unit: "ms", description: "Deadline to confirmed flat.");

        _orderAck = _meter.CreateHistogram<double>(
            OrderAckLatency, unit: "ms", description: "Order transmit to venue acknowledgement.");

        _retentionGaps = _meter.CreateCounter<long>(
            RetentionGaps, unit: "{gap}", description: "Event-log retention gaps observed on read, by consumer group.");

        _pipelineLag = _meter.CreateHistogram<double>(
            PipelineLag, unit: "ms", description: "Event append to consume delay, by consumer group.");

        // Observable: state, not events. A gauge read on scrape reports what is true NOW, which is what a
        // dashboard needs for "are we currently killed / currently degraded".
        _meter.CreateObservableGauge(
            KillSwitchEngaged, () => (long)_killSwitchEngaged, unit: "{engaged}",
            description: "1 while the kill switch is engaged.");

        _meter.CreateObservableGauge(
            OrphanedStops, () => (long)_orphanedStops, unit: "{stop}",
            description: "Working stops currently orphaned — live synthetic-risk exposure.");
    }

    /// <summary>Counts one gate decision.</summary>
    /// <param name="outcome">The gate's verdict.</param>
    /// <param name="bindingLayer">The layer that bound, or <see langword="null"/> when none did.</param>
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer) =>
        _gateDecisions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            // Always present, even when nothing bound: a tag that appears only sometimes changes the series
            // shape between outcomes and breaks aggregation.
            new KeyValuePair<string, object?>("binding_layer", bindingLayer?.ToString() ?? "none"));

    /// <summary>Counts one evaluated flatten deadline — emitted whatever the outcome, including idle.</summary>
    /// <param name="tier">Which tier evaluated it.</param>
    /// <param name="outcome">One of the <c>Flatten*</c> outcome constants.</param>
    public void RecordFlattenDeadline(FlattenTier tier, string outcome) =>
        _flattenDeadlines.Add(
            1,
            new KeyValuePair<string, object?>("tier", TierTag(tier)),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records how long a flatten took to reach confirmed flat.</summary>
    /// <param name="tier">Which tier flattened.</param>
    /// <param name="elapsed">Deadline to flat.</param>
    public void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed) =>
        _timeToFlat.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("tier", TierTag(tier)));

    /// <summary>Records transmit → venue acknowledgement latency.</summary>
    /// <param name="elapsed">The round trip.</param>
    public void RecordOrderAck(TimeSpan elapsed) => _orderAck.Record(elapsed.TotalMilliseconds);

    /// <summary>Sets whether the kill switch is engaged.</summary>
    /// <param name="engaged">Whether it is engaged.</param>
    public void SetKillSwitchEngaged(bool engaged) => Interlocked.Exchange(ref _killSwitchEngaged, engaged ? 1 : 0);

    /// <summary>Sets how many working stops are currently orphaned.</summary>
    /// <param name="count">The count.</param>
    public void SetOrphanedStops(int count) => Interlocked.Exchange(ref _orphanedStops, count);

    /// <summary>Counts one retention gap observed by a consumer (gh#227).</summary>
    /// <param name="consumerGroup">The consumer group whose cursor fell behind.</param>
    public void RecordRetentionGap(string consumerGroup) =>
        _retentionGaps.Add(1, new KeyValuePair<string, object?>("consumer_group", consumerGroup));

    /// <summary>Records append → consume delay for a consumer group.</summary>
    /// <param name="consumerGroup">The consumer group.</param>
    /// <param name="lag">The delay.</param>
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag) =>
        _pipelineLag.Record(lag.TotalMilliseconds, new KeyValuePair<string, object?>("consumer_group", consumerGroup));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static string TierTag(FlattenTier tier) => tier switch
    {
        FlattenTier.Primary => "primary",
        FlattenTier.Watchdog => "watchdog",
        _ => "unknown",
    };
}
