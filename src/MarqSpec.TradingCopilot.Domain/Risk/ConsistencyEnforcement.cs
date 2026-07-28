namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>How the gate treats a breached consistency target (gh#380).</summary>
/// <remarks>
/// Configurable because the rule is unlike every other layer here. A drawdown floor protects <b>capital</b>: pass
/// it and the account is gone, so refusing is the only defensible answer. A consistency target protects
/// <b>payout eligibility</b>: breaching it does not blow the account, it silently disqualifies one that is
/// otherwise passing. Refusing an order for it therefore costs real trading on a day that is going well, while
/// permitting it costs an evaluation — and which of those is worse is the operator's call per account, not a
/// property of the rule. A funded prop account wants <see cref="Block"/>; a personal account with no payout rules
/// wants <see cref="Advisory"/> or no target at all.
/// </remarks>
public enum ConsistencyEnforcement
{
    /// <summary>
    /// Report the consumed fraction and let the order through. The default, deliberately: an unstated posture
    /// must never silently refuse orders on a rule that is not protecting capital, so blocking is opt-in.
    /// </summary>
    Advisory = 0,

    /// <summary>Refuse the order once the target is breached, like any other binding risk layer.</summary>
    Block = 1,
}
