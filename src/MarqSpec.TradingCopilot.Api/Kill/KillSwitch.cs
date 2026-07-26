using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Execution;

namespace MarqSpec.TradingCopilot.Api.Kill;

/// <summary>
/// The process-wide kill-switch state (ADR-0007, R-11, gh#189). The enforcing send path reads
/// <see cref="IKillSwitch.IsEngaged"/> to refuse every outbound order while engaged; the operator's endpoint sets
/// it. It is the fast runtime mirror of the durable <c>KillSwitchState</c> row — set at startup rehydration and on
/// every engage / disengage, so a restart comes up in the state it was left in (the operator's lock persists).
/// </summary>
/// <remarks>
/// State is one immutable snapshot behind a <see langword="volatile"/> reference: the hot-path read
/// (<see cref="IsEngaged"/>, on every send) is lock-free, and a reader always sees a consistent snapshot rather
/// than a half-updated one. Writes (engage / disengage) are rare operator actions.
/// </remarks>
public sealed class KillSwitch : IKillSwitch
{
    private readonly ExecutionMetrics _metrics;
    private volatile KillSwitchStatus _status = KillSwitchStatus.Disengaged;

    /// <summary>Creates the kill switch over the metrics sink that mirrors its state to a dashboard (gh#295).</summary>
    /// <param name="metrics">The execution SLIs; the engaged gauge is set from here on every state change.</param>
    public KillSwitch(ExecutionMetrics metrics) => _metrics = metrics;

    /// <inheritdoc />
    public bool IsEngaged => _status.Engaged;

    /// <summary>The full current state, for display and audit.</summary>
    public KillSwitchStatus Status => _status;

    /// <summary>Engages the kill switch — outbound orders are refused from now until it is disengaged.</summary>
    /// <param name="mode">What engaging did to open positions.</param>
    /// <param name="engagedAt">When it was engaged.</param>
    /// <param name="reason">The operator's reason, if one was given.</param>
    public void Engage(KillSwitchMode mode, DateTimeOffset engagedAt, string? reason)
    {
        _status = new KillSwitchStatus(Engaged: true, mode, engagedAt, reason);

        // The single chokepoint every state change flows through — the operator endpoint, startup rehydration, and
        // the recovery fail-safe all call this — so the gauge is set here once and a killed system shows on the
        // dashboard, not only in a log (gh#295, gh#232). The gauge survives a restart as the durable row does.
        _metrics.SetKillSwitchEngaged(true);
    }

    /// <summary>Disengages the kill switch — outbound orders are allowed again.</summary>
    public void Disengage()
    {
        _status = KillSwitchStatus.Disengaged;
        _metrics.SetKillSwitchEngaged(false);
    }
}

/// <summary>A point-in-time view of the kill switch (ADR-0007, gh#189).</summary>
/// <param name="Engaged">Whether outbound orders are currently disabled.</param>
/// <param name="Mode">What engaging does to open positions.</param>
/// <param name="EngagedAt">When it was engaged, or <see langword="null"/> when disengaged.</param>
/// <param name="Reason">The operator's reason, if one was given.</param>
public sealed record KillSwitchStatus(bool Engaged, KillSwitchMode Mode, DateTimeOffset? EngagedAt, string? Reason)
{
    /// <summary>The disengaged state — outbound orders allowed.</summary>
    public static KillSwitchStatus Disengaged { get; } = new(false, KillSwitchMode.FlattenAll, null, null);
}
