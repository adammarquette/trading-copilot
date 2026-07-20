namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The stacked constraints the gate sizes against — most restrictive wins. Every decision names the layer that
/// bound it, so a trader is never left guessing why a size came back smaller than asked for (R-5).
/// </summary>
public enum RiskLayer
{
    /// <summary>Headroom to the trailing drawdown floor — the account-ending line.</summary>
    DrawdownFloor,

    /// <summary>The account's hard daily loss limit, where the firm imposes one.</summary>
    DailyLossLimit,

    /// <summary>The operator's personal daily-drawdown governor, which sits inside the hard limit.</summary>
    DailyGovernor,

    /// <summary>The daily profit target, once reached with stop-for-the-day enabled.</summary>
    DailyProfitTarget,

    /// <summary>The configured per-trade risk percentage.</summary>
    PerTradeRisk,

    /// <summary>The per-trade hard loss cap, measured against the safety stop.</summary>
    MaxDrawdownPerTrade,

    /// <summary>The operator's manual contract caps, overall or per instrument.</summary>
    ManualCap,

    /// <summary>The execution sanity caps — contracts, notional, and fat-finger bounds (R-16).</summary>
    SanityCap,
}
