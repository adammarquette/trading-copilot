using MarqSpec.TradingCopilot.Domain.Execution;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The durable kill-switch state (ADR-0007, R-11, gh#189): whether outbound orders are disabled, and how. A
/// <b>single row per deployment</b> (one operator, ADR-0017), so the operator's lock <b>survives a restart</b> — it
/// is rehydrated into the runtime <c>KillSwitch</c> at startup, and nothing silently re-enables trading (ADR-0013).
/// System plumbing, not a workspace row — not scoped by the R-20 guard.
/// </summary>
public class KillSwitchState
{
    /// <summary>The fixed key of the single kill-switch row — there is exactly one per deployment.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>The row's key; always <see cref="SingletonId"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether the kill switch is engaged — outbound orders are disabled.</summary>
    public bool Engaged { get; set; }

    /// <summary>What engaging did to open positions (flatten-all or halt-only), for display and audit.</summary>
    public KillSwitchMode Mode { get; set; }

    /// <summary>When it was engaged, or <see langword="null"/> when disengaged.</summary>
    public DateTimeOffset? EngagedAt { get; set; }

    /// <summary>The operator's reason, if one was given.</summary>
    public string? Reason { get; set; }

    /// <summary>When this row was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
