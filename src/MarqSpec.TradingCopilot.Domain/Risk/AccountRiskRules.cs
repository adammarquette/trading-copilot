namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The account's <b>hard</b> rules — imposed by the firm, or by the operator on a live account. Distinct from
/// <see cref="RiskProfile"/>, which is the operator's own tolerance sitting inside these (R-5).
/// </summary>
/// <param name="DailyLossLimit">
/// The hard daily loss limit, where one exists. Apex intraday-trail accounts have none, so this is nullable —
/// absent means unlimited, not zero.
/// </param>
/// <param name="ProfitTarget">The account's profit target, where one applies (evaluation accounts).</param>
/// <param name="FloorSource">Whether the loss floor is firm-imposed or self-imposed.</param>
public sealed record AccountRiskRules(
    decimal? DailyLossLimit,
    decimal? ProfitTarget,
    FloorSource FloorSource);
