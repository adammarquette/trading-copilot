using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;

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
    private readonly IExecutionMetrics _metrics;

    private volatile KillSwitchStatus _status = KillSwitchStatus.Disengaged;

    /// <summary>Creates the flag over the SLI sink.</summary>
    /// <param name="metrics">
    /// The execution SLIs (gh#295). The gauge is set HERE rather than in the endpoint, so every path that moves
    /// the flag moves the metric with it -- including the startup rehydration that restores an operator lock
    /// after a restart (ADR-0013). Wiring it at the endpoint would leave a restarted-but-killed system reading
    /// as healthy on the dashboard.
    /// </param>
    public KillSwitch(IExecutionMetrics metrics)
    {
        _metrics = metrics;
    }

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
        _metrics.SetKillSwitchEngaged(engaged: true);
    }

    /// <summary>Disengages the kill switch — outbound orders are allowed again.</summary>
    public void Disengage()
    {
        _status = KillSwitchStatus.Disengaged;
        _metrics.SetKillSwitchEngaged(engaged: false);
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
