namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// How an account's trailing drawdown floor moves. A property of the <b>account</b>, not the firm — Apex sells
/// both kinds, so it is keyed off the account.
/// </summary>
public enum TrailingMode
{
    /// <summary>
    /// The floor moves only at the close, on the realized closing balance, and is fixed during the next session —
    /// still enforced in real time if touched. Topstep is end-of-day only.
    /// </summary>
    EndOfDay,

    /// <summary>
    /// The floor follows the real-time peak <b>including unrealized P&amp;L</b>, a fixed distance behind, and never
    /// decreases.
    /// </summary>
    Intraday,
}
