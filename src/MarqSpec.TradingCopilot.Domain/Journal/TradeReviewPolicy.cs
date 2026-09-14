namespace MarqSpec.TradingCopilot.Domain.Journal;

/// <summary>
/// Derives whether a trade is "awaiting review" (R-8, gh#1064) — a pure function over what the journal already
/// holds, testable without a database, as <c>OutcomeResolutionPolicy</c> (gh#832) is.
/// </summary>
/// <remarks>
/// <b>Deliberately not a stored column.</b> R-8 frames "awaiting review" as a derived fact — "a closed trade
/// <i>without feedback</i> is flagged awaiting review" — not an operator-set flag, so persisting it as mutable
/// state on <c>Trade</c> or <c>TradeFeedback</c> would create a second source of truth that could drift from
/// reality (the same reason <c>Outcome</c>'s R-15 flags are set only through its own methods). Computing it here
/// from the trade's closed state and its feedback authors keeps exactly one fact in the database — the feedback
/// rows themselves — and this policy re-derives the flag from them every time.
/// </remarks>
public static class TradeReviewPolicy
{
    /// <summary>
    /// A trade is awaiting review when it has <b>closed</b> but no <see cref="FeedbackAuthor.Operator"/>-authored
    /// feedback exists for it yet. An open trade has nothing to review, so it is never "awaiting" — and a future
    /// <see cref="FeedbackAuthor.Ai"/>-authored note (not written by any path today) does not by itself satisfy the
    /// operator's own review.
    /// </summary>
    /// <param name="tradeIsClosed">Whether the trade has closed (its <c>ClosedAt</c> is set).</param>
    /// <param name="feedbackAuthors">The authors of every feedback entry already recorded for the trade.</param>
    /// <returns><see langword="true"/> when the trade is closed and no entry in <paramref name="feedbackAuthors"/> is <see cref="FeedbackAuthor.Operator"/>.</returns>
    public static bool IsAwaitingReview(bool tradeIsClosed, IEnumerable<FeedbackAuthor> feedbackAuthors)
    {
        ArgumentNullException.ThrowIfNull(feedbackAuthors);

        return tradeIsClosed && !feedbackAuthors.Contains(FeedbackAuthor.Operator);
    }
}
