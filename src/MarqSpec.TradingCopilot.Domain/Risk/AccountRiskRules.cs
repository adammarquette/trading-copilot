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
/// <param name="ConsistencyTarget">
/// The consistency target as a fraction — <c>0.4m</c> means "no single day may be more than 40% of cumulative
/// profit" (gh#380). <see langword="null"/> means the account has no such rule, which is the common case: the
/// rule is absent, not zero. Zero would mean "no day may contribute any profit at all", which is not a rule any
/// firm writes.
/// </param>
/// <param name="ConsistencyEnforcement">
/// Whether a breach refuses the order or only warns. Only consulted when <paramref name="ConsistencyTarget"/> is
/// set; see <see cref="Risk.ConsistencyEnforcement"/> for why this is per-account rather than fixed.
/// </param>
public sealed record AccountRiskRules(
    decimal? DailyLossLimit,
    decimal? ProfitTarget,
    FloorSource FloorSource,
    decimal? ConsistencyTarget = null,
    ConsistencyEnforcement ConsistencyEnforcement = ConsistencyEnforcement.Advisory);
