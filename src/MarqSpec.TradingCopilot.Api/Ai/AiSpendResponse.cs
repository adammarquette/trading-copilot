namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The operator's AI spend over a period (gh#741, ADR-0008 / ADR-0015) — read from the durable <c>AIUsage</c> ledger,
/// never the export-only Prometheus meter (ADR-0002). Owner-scoped (R-20): the operator's OWN bill — "your keys, your
/// bill" (gh#62). The governor's cap is <b>daily</b>, so <see cref="TodayUsd"/> against <see cref="DailyBudgetUsd"/> is
/// the live "against the cap" figure; <see cref="TotalUsd"/> is the period's context.
/// </summary>
/// <param name="From">The inclusive start of the reported period (UTC).</param>
/// <param name="To">The inclusive end of the reported period (UTC).</param>
/// <param name="TotalUsd">Total spend over the period.</param>
/// <param name="TodayUsd">Spend so far on the current Central trading day — the window the daily cap resets on.</param>
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
