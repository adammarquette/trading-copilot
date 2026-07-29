using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The platform-level AI-spend governor's configuration (gh#448, ADR-0008): a single daily dollar budget the operator
/// sets for <b>all</b> AI spend deployment-wide (R-20 — one shared account funds every user). Static deployment
/// config (the Options pattern, like <see cref="LlmOptions"/> / <c>CohereOptions</c> / <c>FlattenOptions</c>), not a
/// persisted per-account entity — a platform cap has no per-account home.
/// </summary>
/// <remarks>
/// <b>Opt-in and inert by default.</b> <see cref="DailyBudgetUsd"/> absent (null) leaves the governor off — the
/// no-cap status quo — so a fresh deploy is never surprised by a paused co-pilot. A spend cap has no sane universal
/// default and, unlike the safety-critical always-on auto-flatten, an absent cost cap hides nothing. Its presence
/// <i>is</i> the switch (the <see cref="LlmOptions.IsConfigured"/> posture), so there is no separate enable flag to
/// desync from the value.
/// </remarks>
public sealed class GovernorOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Governor";

    /// <summary>
    /// The daily AI-spend cap in USD. <see langword="null"/> (or absent) leaves the governor <b>inert</b> — no cap.
    /// When set it must be positive (validated on start).
    /// </summary>
    public decimal? DailyBudgetUsd { get; init; }

    /// <summary>The fraction of the budget at which a heads-up alert fires — in (0, 1]. Defaults to 0.8 (80%).</summary>
    public decimal AlertThresholdFraction { get; init; } = 0.8m;

    /// <summary>Whether the governor is configured to enforce — a positive budget is present.</summary>
    public bool IsActive => DailyBudgetUsd is > 0m;

    /// <summary>
    /// Projects the config to the domain <see cref="AiSpendBudget"/>, or <see langword="null"/> when inert — the seam
    /// the scan reads once at construction (mirrors <c>SalienceOptions.ToParameters</c>).
    /// </summary>
    /// <returns>The declared budget, or <see langword="null"/> when no cap is configured.</returns>
    public AiSpendBudget? ToBudget() =>
        IsActive ? AiSpendBudget.Declare(DailyBudgetUsd!.Value, AlertThresholdFraction) : null;
}
