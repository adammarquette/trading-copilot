namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>What the AI-spend governor decided.</summary>
public enum AiSpendVerdict
{
    /// <summary>Spend is under budget — the AI call may proceed.</summary>
    Allow,

    /// <summary>The daily budget is reached — the AI call is blocked (no invocation, no spend).</summary>
    Block,
}

/// <summary>
/// The operator's platform-level AI-spend budget (gh#448, ADR-0008) — a single daily dollar cap over <b>all</b> AI
/// invocations (one shared LLM + embeddings account funds every user, R-20). Constructed through <see cref="Declare"/>
/// so a budget that would divide-by-zero or alert nonsensically never reaches the gate.
/// </summary>
/// <param name="DailyBudgetUsd">The daily spend cap in USD — positive.</param>
/// <param name="AlertThresholdFraction">The fraction of the budget at which to pre-alert — in (0, 1].</param>
public sealed record AiSpendBudget(decimal DailyBudgetUsd, decimal AlertThresholdFraction)
{
    /// <summary>The dollar figure the pre-alert fires at.</summary>
    public decimal ThresholdUsd => DailyBudgetUsd * AlertThresholdFraction;

    /// <summary>
    /// Declares a budget with its invariants enforced (the <see cref="Risk.RiskProfile.Declare"/> pattern): a
    /// non-positive cap or an out-of-range threshold throws here rather than mis-capping at the gate.
    /// </summary>
    /// <param name="dailyBudgetUsd">The daily cap in USD — positive.</param>
    /// <param name="alertThresholdFraction">The pre-alert fraction — in (0, 1].</param>
    /// <returns>The validated budget.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its declared range.</exception>
    public static AiSpendBudget Declare(decimal dailyBudgetUsd, decimal alertThresholdFraction)
    {
        if (dailyBudgetUsd <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dailyBudgetUsd), dailyBudgetUsd, "The daily AI-spend budget must be positive.");
        }

        if (alertThresholdFraction <= 0m || alertThresholdFraction > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alertThresholdFraction), alertThresholdFraction, "The alert threshold fraction must be in (0, 1].");
        }

        return new AiSpendBudget(dailyBudgetUsd, alertThresholdFraction);
    }
}

/// <summary>
/// What the governor decided, and why — mirrors <see cref="Risk.GateDecision"/>. The <see cref="Reason"/> is always
/// populated (a blocked review's advisory quotes it to the operator).
/// </summary>
/// <param name="Verdict">Allow or Block.</param>
/// <param name="BudgetUsd">The daily budget the decision was made against.</param>
/// <param name="SpentUsd">The spend-in-window the decision was made against.</param>
/// <param name="ConsumedFraction">Spend as a fraction of the budget (may exceed 1 when over).</param>
/// <param name="ThresholdReached">Whether spend has reached the pre-alert threshold.</param>
/// <param name="Reason">A human-readable explanation, always populated.</param>
public sealed record AiSpendDecision(
    AiSpendVerdict Verdict,
    decimal BudgetUsd,
    decimal SpentUsd,
    decimal ConsumedFraction,
    bool ThresholdReached,
    string Reason)
{
    /// <summary>Whether the AI call must not be made.</summary>
    public bool IsBlocked => Verdict == AiSpendVerdict.Block;
}

/// <summary>
/// The platform-level AI-spend gate (gh#448, ADR-0008) — a <b>pure, deterministic</b> check that caps AI invocations
/// against the operator's daily budget, mirroring the R-5 daily risk governor (<see cref="Risk.IRiskGate"/>):
/// <c>budget − spent ≤ 0 ⇒ Block</c>. It gates <b>whether an AI call is made</b> (cost), never what the model
/// proposes — enforcement lives below the model.
/// </summary>
/// <remarks>
/// Dependency-free by design: the caller supplies the spend-in-window (a windowed sum of the <c>AIUsage</c> ledger),
/// exactly as the risk gate is handed the consumed daily loss in its context — so the gate stays trivially
/// unit-testable and reaches no clock, DB, or execution type.
/// </remarks>
public interface IAiSpendGovernor
{
    /// <summary>Decides whether an AI call may proceed under the budget.</summary>
    /// <param name="budget">The operator's daily budget.</param>
    /// <param name="spentInWindowUsd">Spend so far in the current window (the ledger floor), never negative.</param>
    /// <returns>The decision — <see cref="AiSpendVerdict.Block"/> once spend reaches the budget.</returns>
    AiSpendDecision Evaluate(AiSpendBudget budget, decimal spentInWindowUsd);
}

/// <inheritdoc cref="IAiSpendGovernor" />
public sealed class AiSpendGovernor : IAiSpendGovernor
{
    /// <inheritdoc />
    public AiSpendDecision Evaluate(AiSpendBudget budget, decimal spentInWindowUsd)
    {
        ArgumentNullException.ThrowIfNull(budget);

        // Spend can only be a non-negative sum; clamp defensively so a bad caller can never make the fraction negative.
        decimal spent = spentInWindowUsd < 0m ? 0m : spentInWindowUsd;
        decimal consumedFraction = spent / budget.DailyBudgetUsd; // budget > 0 by Declare, so never divides by zero
        bool thresholdReached = spent >= budget.ThresholdUsd;

        // FAIL-CLOSED on the cap (the point of the governor), the exact mirror of RiskGate's
        // `governorRemaining <= 0 => Block`: once the floor of measured spend reaches the budget, no further call.
        if (spent >= budget.DailyBudgetUsd)
        {
            return new AiSpendDecision(
                AiSpendVerdict.Block,
                budget.DailyBudgetUsd,
                spent,
                consumedFraction,
                ThresholdReached: true,
                FormattableString.Invariant(
                    $"The daily AI-spend budget of {budget.DailyBudgetUsd:0.####} USD is reached (spent {spent:0.####} USD)."));
        }

        return new AiSpendDecision(
            AiSpendVerdict.Allow,
            budget.DailyBudgetUsd,
            spent,
            consumedFraction,
            thresholdReached,
            FormattableString.Invariant(
                $"AI spend is {spent:0.####} of the {budget.DailyBudgetUsd:0.####} USD daily budget."));
    }
}
