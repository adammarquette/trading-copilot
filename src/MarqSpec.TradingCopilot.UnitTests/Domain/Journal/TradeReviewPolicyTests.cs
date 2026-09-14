using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Journal;

public class TradeReviewPolicyTests
{
    // --- "Awaiting review" is derived, never stored: closed + no operator feedback yet (R-8, gh#1064) ---

    [Fact]
    public void IsAwaitingReview_ShouldBeTrue_WhenTheTradeIsClosedAndCarriesNoFeedback()
    {
        TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: true, feedbackAuthors: [])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAwaitingReview_ShouldBeFalse_WhenTheTradeIsNotClosedYet()
    {
        // An open trade has nothing to review yet -- never "awaiting review", regardless of feedback.
        TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: false, feedbackAuthors: [])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAwaitingReview_ShouldBeFalse_WhenTheOperatorHasLeftFeedback()
    {
        TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: true, feedbackAuthors: [FeedbackAuthor.Operator])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAwaitingReview_ShouldStayTrue_WhenOnlyAiFeedbackExists()
    {
        // An AI-authored note alone must never satisfy the OPERATOR's own review (R-8's "awaiting review" is about
        // the operator's attention specifically).
        TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: true, feedbackAuthors: [FeedbackAuthor.Ai])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAwaitingReview_ShouldBeFalse_WhenOperatorFeedbackIsMixedWithAiFeedback()
    {
        TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: true, feedbackAuthors: [FeedbackAuthor.Ai, FeedbackAuthor.Operator])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAwaitingReview_ShouldThrow_WhenFeedbackAuthorsIsNull()
    {
        Action act = () => TradeReviewPolicy.IsAwaitingReview(tradeIsClosed: true, feedbackAuthors: null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
