using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Domain.Observability;

/// <summary>Which auto-flatten tier produced a signal (gh#232) — the two must never be merged.</summary>
public enum FlattenTier
{
    /// <summary>The primary scheduler (gh#185).</summary>
    Primary,

    /// <summary>The independent redundant watchdog (gh#187).</summary>
    Watchdog,
}

/// <summary>
/// The execution-SLI sink (gh#232, gh#295) — the seam the enforcing paths record through.
/// </summary>
/// <remarks>
/// <para>
/// A seam in <c>Domain</c> for the same reason <see cref="Events.IEventLog"/> and the notification channel are:
/// the <b>send path lives here</b>, so measuring transmit → acknowledgement is impossible from the Api layer, and
/// Domain must not depend on it. The concrete <c>System.Diagnostics.Metrics</c> implementation stays in the Api
/// composition root, exactly as the Timescale event log stays in Data.
/// </para>
/// <para>
/// <b>Every implementation must be total.</b> Nothing here may throw: a metrics fault must never fail the trading
/// action it was measuring (engineering §9). Callers therefore record without a try/catch, and that is safe by
/// contract rather than by luck.
/// </para>
/// </remarks>
public interface IExecutionMetrics
{
    /// <summary>Counts one gate decision.</summary>
    /// <param name="outcome">The gate's verdict.</param>
    /// <param name="bindingLayer">The layer that bound, or <see langword="null"/> when none did.</param>
    void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer);

    /// <summary>Counts one evaluated flatten deadline — emitted whatever the outcome, including idle.</summary>
    /// <param name="tier">Which tier evaluated it.</param>
    /// <param name="outcome">One of the sink's outcome constants.</param>
    void RecordFlattenDeadline(FlattenTier tier, string outcome);

    /// <summary>Records how long a flatten took to reach confirmed flat.</summary>
    /// <param name="tier">Which tier flattened.</param>
    /// <param name="elapsed">Deadline to flat.</param>
    void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed);

    /// <summary>Records transmit → venue acknowledgement latency.</summary>
    /// <param name="elapsed">The round trip.</param>
    void RecordOrderAck(TimeSpan elapsed);

    /// <summary>Sets whether the kill switch is engaged.</summary>
    /// <param name="engaged">Whether it is engaged.</param>
    void SetKillSwitchEngaged(bool engaged);

    /// <summary>Sets how many working stops are currently orphaned.</summary>
    /// <param name="count">The count.</param>
    void SetOrphanedStops(int count);

    /// <summary>Counts one retention gap observed by a consumer (gh#227).</summary>
    /// <param name="consumerGroup">The consumer group whose cursor fell behind.</param>
    void RecordRetentionGap(string consumerGroup);

    /// <summary>Records append → consume delay for a consumer group.</summary>
    /// <param name="consumerGroup">The consumer group.</param>
    /// <param name="lag">The delay.</param>
    void RecordPipelineLag(string consumerGroup, TimeSpan lag);
}

/// <summary>
/// A sink that records nothing — the default where telemetry is not wired (tests, a host that never composed it).
/// </summary>
/// <remarks>
/// Exists so a caller never needs a null check on a metrics dependency. A missing sink must degrade to silence,
/// never to a branch on the trading path.
/// </remarks>
public sealed class NullExecutionMetrics : IExecutionMetrics
{
    /// <summary>The shared instance.</summary>
    public static NullExecutionMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer)
    {
    }

    /// <inheritdoc />
    public void RecordFlattenDeadline(FlattenTier tier, string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void RecordOrderAck(TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void SetKillSwitchEngaged(bool engaged)
    {
    }

    /// <inheritdoc />
    public void SetOrphanedStops(int count)
    {
    }

    /// <inheritdoc />
    public void RecordRetentionGap(string consumerGroup)
    {
    }

    /// <inheritdoc />
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag)
    {
    }
}
