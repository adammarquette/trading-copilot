namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The operator's AI spend (gh#741, ADR-0008 / ADR-0015) — read from the durable <c>AIUsage</c> ledger, never the
/// export-only Prometheus meter (ADR-0002). <b>Two scopes, deliberately.</b> The <b>period</b> breakdown
/// (<see cref="TotalUsd"/> / <see cref="ByModel"/> / <see cref="ByDay"/>) is the operator's OWN <i>decision</i> spend —
/// R-20 owner-scoped, "your keys, your bill" (gh#62) — while <see cref="TodayUsd"/> is <b>deployment-wide</b>, matching
/// exactly what the governor's daily cap enforces. So <see cref="TodayUsd"/> against <see cref="DailyBudgetUsd"/> is the
/// honest "against the cap" figure: it cannot read "under cap" while the co-pilot has already been paused by spend the
/// operator's own decisions did not incur (the continuous embed-infra spend, owned by the SystemOwner sentinel).
/// </summary>
/// <param name="From">The inclusive start of the reported period (UTC).</param>
/// <param name="To">The inclusive end of the reported period (UTC).</param>
/// <param name="TotalUsd">Total spend over the period.</param>
/// <param name="TodayUsd">Deployment-wide spend so far on the current Central trading day — the exact figure the governor's daily cap enforces against (every owner, incl. the SystemOwner embed-infra rows).</param>
/// <param name="DailyBudgetUsd">The governor's daily cap, or <see langword="null"/> when no cap is configured (inert).</param>
/// <param name="ByModel">Period spend per model, highest first.</param>
/// <param name="ByDay">Period spend per Central trading day, earliest first.</param>
public sealed record AiSpendResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    decimal TotalUsd,
    decimal TodayUsd,
    decimal? DailyBudgetUsd,
    IReadOnlyList<AiSpendModelSlice> ByModel,
    IReadOnlyList<AiSpendDaySlice> ByDay);

/// <summary>One model's share of period spend.</summary>
/// <param name="Model">The billed model id (e.g. <c>claude-haiku-4-5</c>).</param>
/// <param name="CostUsd">The model's total estimated cost over the period.</param>
public sealed record AiSpendModelSlice(string Model, decimal CostUsd);

/// <summary>One Central trading day's spend.</summary>
/// <param name="Day">The Central trading day.</param>
/// <param name="CostUsd">That day's total estimated cost.</param>
public sealed record AiSpendDaySlice(DateOnly Day, decimal CostUsd);
