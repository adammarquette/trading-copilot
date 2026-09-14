namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>What the kill switch does to <b>open positions</b> when engaged (ADR-0007, R-11, gh#189).</summary>
/// <remarks>
/// Either mode disables outbound orders and cancels working orders; they differ only in what happens to positions
/// already open. <see cref="FlattenAll"/> is the zero value on purpose — the fail-safe default is to close
/// everything, so an unspecified or defaulted mode errs toward flat rather than toward carrying risk.
/// </remarks>
public enum KillSwitchMode
{
    /// <summary>Market-flatten every open position via the native-first flatten sequence (hold-to-confirm). The default.</summary>
    FlattenAll,

    /// <summary>Leave open positions on their native safety stops — cancel working orders and block new ones only.</summary>
    HaltOnly,
}
