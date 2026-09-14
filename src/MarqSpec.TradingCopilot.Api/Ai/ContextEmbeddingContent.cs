using System.Globalization;
using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Renders an embeddable row as the text every retrieval stage agrees on (gh#1065) — the single source of truth for
/// "what does this row say".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is public and pure rather than a private helper.</b> Three stages must agree on this text byte for
/// byte. The embed pass hashes it (<c>EmbeddingContentHash</c>) to decide whether a paid re-embed is needed, so an
/// unstable rendering re-bills the operator on <i>every</i> pass; the retrieval pipeline hands it to the cross-encoder
/// reranker, so a different rendering there would rank rows on text their vectors were never built from; and the
/// grounding envelope shows a trimmed form of it. One function, unit-tested, keeps those three honest.
/// </para>
/// <para>
/// <b>Culture-invariant by construction.</b> Prices and P&amp;L are <c>decimal</c> and formatted with
/// <see cref="CultureInfo.InvariantCulture"/>: a culture-sensitive separator would change the content hash the moment
/// the host's culture did, silently re-billing every stored row.
/// </para>
/// <para>
/// <b>The trade line carries system facts only.</b> A suggestion's <see cref="Suggestion.Rationale"/> is model-authored
/// prose — untrusted display data — so it stays in the document body, where the grounding envelope labels the whole
/// block as data rather than instruction. It never leaks into the one-line title a consumer renders as a heading.
/// </para>
/// </remarks>
public static class ContextEmbeddingContent
{
    /// <summary>The text embedded for a news item — headline then summary, the shape gh#377's pass has always used.</summary>
    /// <param name="news">The news record.</param>
    /// <returns>The document text for that news item.</returns>
    public static string ForNews(NewsRecord news)
    {
        ArgumentNullException.ThrowIfNull(news);
        return $"{news.Title}\n\n{news.Summary}";
    }

    /// <summary>The text embedded for a suggestion — its trade line, then the model's rationale.</summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <returns>The document text for that suggestion.</returns>
    public static string ForSuggestion(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        string line = SuggestionLine(suggestion);

        // An empty rationale is legal (the entity guarantees "empty string, never null"), and appending a blank body
        // would both waste tokens and change the hash of a row whose meaning did not change.
        return suggestion.Rationale.Length == 0 ? line : $"{line}\n\n{suggestion.Rationale}";
    }

    /// <summary>The one-line, system-authored summary of a suggestion's proposed trade.</summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <returns>The trade line, for example <c>Suggested ES Buy 2 @ 5000.25 (stop 4990.00, target 5020.50)</c>.</returns>
    public static string SuggestionLine(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Suggested {suggestion.Instrument} {suggestion.Side} {suggestion.Size} @ {suggestion.EntryPrice} "
            + $"(stop {suggestion.StopPrice}, target {suggestion.TargetPrice})");
    }

    /// <summary>The text embedded for a journal entry — its closed-trade line, then its realized result.</summary>
    /// <param name="trade">The journaled trade.</param>
    /// <returns>The document text for that journal entry.</returns>
    public static string ForJournalEntry(Trade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return $"{JournalEntryLine(trade)}\n\n{JournalEntryDetail(trade)}";
    }

    /// <summary>The one-line summary of a journaled trade's terms.</summary>
    /// <param name="trade">The journaled trade.</param>
    /// <returns>The trade line, for example <c>Closed ES Buy 2 @ 5000.25 -&gt; 5010.50</c>.</returns>
    public static string JournalEntryLine(Trade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        // "still open" rather than a fabricated exit: only closed trades are embedded, but a row can legitimately lack
        // an exit price (journaled before the production writer existed), and a blank would read as a zero-price exit.
        string exit = trade.ExitPrice is { } price
            ? price.ToString(CultureInfo.InvariantCulture)
            : "still open";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Closed {trade.Instrument} {trade.Side} {trade.Size} @ {trade.EntryPrice} -> {exit}");
    }

    /// <summary>The realized result of a journaled trade, named so a semantic query for "my losers" can match it.</summary>
    /// <param name="trade">The journaled trade.</param>
    /// <returns>The realized-result sentence.</returns>
    public static string JournalEntryDetail(Trade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        if (trade.RealizedPnL is not { } realized)
        {
            return "Not yet realized."; // honest absence -- never a fabricated zero, which would read as a scratch
        }

        // Naming the outcome in words is what makes the vector useful: "how did my losers go" has nothing to match
        // against a bare signed number, and the reranker cross-encodes this same text.
        string verdict = realized switch
        {
            > 0m => "a winner",
            < 0m => "a loser",
            _ => "a scratch",
        };

        return string.Create(CultureInfo.InvariantCulture, $"Realized {realized}, {verdict}.");
    }
}
