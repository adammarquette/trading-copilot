using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data.Journal;

/// <summary>
/// Reads a trade's post-close feedback (gh#1064, R-8) — the input <c>TradeFeedbackEndpoints</c> composes into the
/// read surface and <c>TradeReviewPolicy</c> derives "awaiting review" from.
/// </summary>
public static class TradeFeedbackReader
{
    /// <summary>
    /// The <paramref name="tradeId"/>'s feedback entries, oldest first. Owner-scoped by the R-20 query filter
    /// (<see cref="TradeFeedback"/> is <c>IUserOwned</c>): a request read, so it sees only the caller's own rows.
    /// </summary>
    /// <param name="database">The scoped, R-20-filtered context.</param>
    /// <param name="tradeId">The trade whose feedback to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The trade's feedback, oldest first; empty (not an error) when none has been recorded.</returns>
    public static async Task<IReadOnlyList<TradeFeedback>> FeedbackForTradeAsync(
        this TradingCopilotDbContext database,
        Guid tradeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await database.TradeFeedbacks
            .Where(feedback => feedback.TradeId == tradeId)
            .OrderBy(feedback => feedback.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
