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

        ReviewOutcome outcome = await reviewer.ReviewAsync(context, CancellationToken.None);

        outcome.Should().BeOfType<ReviewOutcome.Suppress>()
            .Which.Reason.Should().Be(SuppressReason.NoReviewerConfigured);
    }
}
