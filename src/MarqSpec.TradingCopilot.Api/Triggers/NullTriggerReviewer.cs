using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The honest inert reviewer (R-4 / ADR-0008): used when no LLM is configured. It makes <b>no</b> LLM call and always
/// returns <see cref="ReviewOutcome.Suppress"/> with <see cref="SuppressReason.NoReviewerConfigured"/>.
/// </summary>
/// <remarks>
/// Inert but never silent — the mirror of <c>NullNotificationChannel</c>. The agent-review route still fires, still
/// journals the firing, and still <i>tells the operator</i> that a setup fired which needs review but could not be
/// reviewed (the evaluation service turns this reason into a fallback advisory). Bound in place of
/// <see cref="LlmTriggerReviewer"/> exactly when <c>LlmOptions.IsConfigured</c> is false, so the route is always wired
/// to something honest rather than optional and silently absent.
/// </remarks>
public sealed class NullTriggerReviewer : ITriggerReviewer
{
    /// <inheritdoc />
    public decimal EstimatedDeepCallCostUsd => 0m; // it never calls, so an escalation would cost nothing.

    /// <inheritdoc />
    public Task<AgentReview> ReviewAsync(
        TriggerReviewContext context, CancellationToken cancellationToken, bool allowEscalate = true)
    {
        _ = allowEscalate; // there is no escalation to permit -- this reviewer makes no call at all.

        // No LLM call is made, so no spend to ledger -- Costs is empty.
        return Task.FromResult(new AgentReview(
            new ReviewOutcome.Suppress(SuppressReason.NoReviewerConfigured, "no LLM reviewer is configured"), Costs: []));
    }
}
