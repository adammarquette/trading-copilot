namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Where the loss floor comes from. The risk-budget dynamic is identical either way — only the origin and the
/// consequence of a breach differ (R-5).
/// </summary>
public enum FloorSource
{
    /// <summary>A prop-firm rule. Breaching it <b>fails the account</b>.</summary>
    FirmImposed,

    /// <summary>
    /// The operator's own cap on a live or brokerage account — e.g. fund with $50K but cap the loss at $10K.
    /// Breaching it halts new entries and flattens: a personal circuit breaker, not a lost account.
    /// </summary>
    SelfImposed,
}
