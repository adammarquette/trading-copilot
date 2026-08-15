using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Api.Observability;

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
public sealed class ExecutionMetrics : IExecutionMetrics, IDisposable
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

    /// <summary>
    /// How much of a blind window the bar store could not cover on recovery (gh#482). A histogram: its `_count`
    /// answers "did it happen" for the rule, its distribution answers "how bad" for the operator.
    /// </summary>
    public const string BackfillShortfall = "trading.stops.backfill_shortfall";

    /// <summary>Positions the venue reports open — the "…with exposure" qualifier ADR-0019 conditions need.</summary>
    public const string OpenPositions = "trading.positions.open";

    /// <summary>
    /// Open positions with <b>no protective stop resting at the venue</b> — ADR-0019's P1, and the thing the
    /// staged-stop model exists to make impossible.
    /// </summary>
    public const string UnprotectedPositions = "trading.positions.unprotected";

    /// <summary>Whether the venue connection is up (1/0).</summary>
    public const string VenueConnected = "trading.venue.connected";

    /// <summary>
    /// Round-trip journaling outcomes, dimensioned by outcome (gh#731) — emitted for every flat that reached the
    /// composition stage, so "journaling has stopped" is distinguishable from "no trips closed".
    /// </summary>
    public const string JournalOutcomes = "trading.journal.outcomes";

    /// <summary>Outcome tag: the round trip was journalled.</summary>
    public const string JournalWritten = "journalled";

    /// <summary>Outcome tag: the fills are <b>genuinely ambiguous</b> — an unclassifiable fill side, or a same-instant
    /// opposite-side tie whose direction is undecidable (gh#759 refuse-don't-guess). A window that merely does not yet
    /// reconcile to flat (a closing fill not yet ingested) is <see cref="JournalDeferred"/> (gh#748), not this.</summary>
    public const string JournalNotComposable = "not-composable";

    /// <summary>
    /// Outcome tag: the flat's fills do not <b>yet</b> reconcile to a completed round trip because a closing fill is
    /// missing or not yet ingested — the flat callback beat the fill callback (gh#748). Deferred and retried when a
    /// fill for the account lands, rather than mislabelled <see cref="JournalNotComposable"/> and lost (which would
    /// leave the round trip's realized P&amp;L out of the daily governor permanently). An account showing this but
    /// never resolving to <see cref="JournalWritten"/> has a closing fill that never arrived — investigate.
    /// </summary>
    public const string JournalDeferred = "deferred";

    /// <summary>Outcome tag: the composition spanned an already-journalled close (a same-instant boundary merge) and was refused.</summary>
    public const string JournalBoundaryMergeRefused = "boundary-merge-refused";

    /// <summary>Outcome tag: fail-closed — the entry order snapshotted no point value, so the P&amp;L is unknowable.</summary>
    public const string JournalNoPointValue = "no-point-value";

    /// <summary>Outcome tag: a replay recomposed an already-journalled trip; the idempotent pre-check skipped it.</summary>
    public const string JournalAlreadyJournalled = "already-journalled";

    /// <summary>Outcome tag: a concurrent writer had journalled it first; the unique index rejected this write.</summary>
    public const string JournalDuplicateRejected = "duplicate-rejected";

    /// <summary>
    /// Outcome tag: the write FAILED on a non-idempotent fault (a CHECK / FK violation, a serialization failure, a
    /// lost connection) — a real, non-dupe error the writer now surfaces rather than swallowing as a benign skip
    /// (gh#747). An account showing this is losing Trade rows: the day's realized P&amp;L under-reports until corrected.
    /// </summary>
    public const string JournalWriteFailed = "write-failed";

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

    /// <summary>
    /// Outcome tag: the deadline is near with exposure open — the escalating warning that precedes the flatten
    /// (R-13). Journalled as <c>flatten.warning</c> since gh#12; metered since gh#370.
    /// </summary>
    public const string FlattenWarning = "warning";

    /// <summary>
    /// Outcome tag: the market has <b>no configured deadline at all</b> — distinct from <see cref="FlattenDisabled"/>
    /// (gh#370). Deliberately disabled and never configured are different operator errors, and only one of them
    /// is a surprise; folding them together hid that.
    /// </summary>
    public const string FlattenUnconfigured = "unconfigured";

    /// <summary>
    /// Outcome tag: the pass holds a row for an account the venue's live roster does not report, so it could not be
    /// evaluated this pass (gh#527). Distinct from the deadline dispositions above — the account was never reached,
    /// no deadline was read — but recorded on the same series so a persistent roster gap can raise an alert rather
    /// than living only in the event log (the gh#370 "journalled but never metered" lesson).
    /// </summary>
    public const string FlattenUnrostered = "unrostered";

    /// <summary>
    /// Outcome tag: a close attempt came back with exposure still open — a partial fill, a silent reject, or a
    /// faulted call (gh#370). Previously indistinguishable from <see cref="FlattenEscalated"/>, which ADR-0019
    /// separates because escalation means <i>attempts exhausted</i> while this means <i>one attempt bounced</i>.
    /// </summary>
    public const string FlattenRejected = "rejected";

    private readonly Meter _meter;
    private readonly Counter<long> _gateDecisions;
    private readonly Counter<long> _flattenDeadlines;
    private readonly Histogram<double> _timeToFlat;
    private readonly Histogram<double> _orderAck;
    private readonly Counter<long> _retentionGaps;
    private readonly Histogram<double> _pipelineLag;
    private readonly Histogram<double> _backfillShortfall;
    private readonly Counter<long> _journalOutcomes;

    private int _killSwitchEngaged;
    private int _orphanedStops;
    private int _openPositions;
    private int _unprotectedPositions;
    private int _venueConnected = 1; // assume up until told otherwise; the monitor corrects on its first pass

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="meterName">
    /// Overrides the meter name. Production leaves this null and gets <see cref="MeterName"/>; a test passes a
    /// unique name so its <c>MeterListener</c> observes <b>only its own</b> instance. Without that isolation a
    /// listener filtering on the shared name also receives measurements from instances in test classes running
    /// in parallel — which is a data race on the listener's buffer, not a hypothetical.
    /// </param>
    public ExecutionMetrics(string? meterName = null)
    {
        _meter = new Meter(meterName ?? MeterName);

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

        _backfillShortfall = _meter.CreateHistogram<double>(
            BackfillShortfall, unit: "ms",
            description: "Blind-window duration the bar store could not cover on recovery, by contract.");

        _journalOutcomes = _meter.CreateCounter<long>(
            JournalOutcomes, unit: "{outcome}", description: "Round-trip journaling outcomes, by outcome.");

        // Observable: state, not events. A gauge read on scrape reports what is true NOW, which is what a
        // dashboard needs for "are we currently killed / currently degraded".
        _meter.CreateObservableGauge(
            KillSwitchEngaged, () => (long)_killSwitchEngaged, unit: "{engaged}",
            description: "1 while the kill switch is engaged.");

        _meter.CreateObservableGauge(
            OrphanedStops, () => (long)_orphanedStops, unit: "{stop}",
            description: "Working stops currently orphaned — live synthetic-risk exposure.");

        _meter.CreateObservableGauge(
            OpenPositions, () => (long)_openPositions, unit: "{position}",
            description: "Positions the venue reports open.");

        _meter.CreateObservableGauge(
            UnprotectedPositions, () => (long)_unprotectedPositions, unit: "{position}",
            description: "Open positions with no protective stop resting at the venue.");

        _meter.CreateObservableGauge(
            VenueConnected, () => (long)_venueConnected, unit: "{connected}",
            description: "1 while the venue connection is up.");
    }

    /// <inheritdoc />
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer) =>
        _gateDecisions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            // Always present, even when nothing bound: a tag that appears only sometimes changes the series
            // shape between outcomes and breaks aggregation.
            new KeyValuePair<string, object?>("binding_layer", bindingLayer?.ToString() ?? "none"));

    /// <summary>Counts one flatten-pass disposition — mostly one evaluated deadline, the exception being <c>unrostered</c> (gh#527), an account the pass never reached. Emitted whatever the outcome, including idle.</summary>
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

    /// <inheritdoc />
    public void SetPositionProtection(int open, int unprotected)
    {
        Interlocked.Exchange(ref _openPositions, open);
        Interlocked.Exchange(ref _unprotectedPositions, unprotected);
    }

    /// <inheritdoc />
    public void SetVenueConnected(bool connected) => Interlocked.Exchange(ref _venueConnected, connected ? 1 : 0);

    /// <summary>Counts one retention gap observed by a consumer (gh#227).</summary>
    /// <param name="consumerGroup">The consumer group whose cursor fell behind.</param>
    public void RecordRetentionGap(string consumerGroup) =>
        _retentionGaps.Add(1, new KeyValuePair<string, object?>("consumer_group", consumerGroup));

    /// <summary>Records append → consume delay for a consumer group.</summary>
    /// <param name="consumerGroup">The consumer group.</param>
    /// <param name="lag">The delay.</param>
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag) =>
        _pipelineLag.Record(lag.TotalMilliseconds, new KeyValuePair<string, object?>("consumer_group", consumerGroup));

    /// <summary>Records an uncovered blind-window duration for one contract (gh#482).</summary>
    /// <param name="contractKey">The venue contract whose window was not fully covered.</param>
    /// <param name="uncovered">How much of the window has no bars in the store.</param>
    public void RecordBackfillShortfall(string contractKey, TimeSpan uncovered) =>
        _backfillShortfall.Record(
            uncovered.TotalMilliseconds, new KeyValuePair<string, object?>("contract", contractKey));

    /// <inheritdoc />
    public void RecordTradeJournalOutcome(string outcome) =>
        _journalOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static string TierTag(FlattenTier tier) => tier switch
    {
        FlattenTier.Primary => "primary",
        FlattenTier.Watchdog => "watchdog",
        _ => "unknown",
    };
}
