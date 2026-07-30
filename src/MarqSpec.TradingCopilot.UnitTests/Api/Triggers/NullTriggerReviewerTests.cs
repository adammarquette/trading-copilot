using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The honest inert reviewer (gh#402, ADR-0008): with no LLM configured it makes no call and suppresses with
/// <see cref="SuppressReason.NoReviewerConfigured"/> — the reason the evaluation service turns into a fallback
/// advisory, so a fired setup is never silently dropped.
/// </summary>
public class NullTriggerReviewerTests
{
    [Fact]
    public async Task ReviewAsync_ShouldSuppressWithNoReviewerConfigured_AndMakeNoLlmCall()
    {
        NullTriggerReviewer reviewer = new();
        TriggerReviewContext context = new(
            Guid.NewGuid(), InstrumentId.Parse("ES"), "rsi", 14, 1,
            IndicatorComparison.Below, 30m, 25m, DateTimeOffset.UnixEpoch);

        AgentReview review = await reviewer.ReviewAsync(context, CancellationToken.None);

        review.Outcome.Should().BeOfType<ReviewOutcome.Suppress>()
            .Which.Reason.Should().Be(SuppressReason.NoReviewerConfigured);

        // No LLM call was made, so there is NO spend to ledger -- the inert reviewer's Costs is EMPTY (gh#449). The
        // scan records one usage row per cost, so an empty Costs is what makes it record nothing at all.
        review.Costs.Should().BeEmpty();
    }
}
