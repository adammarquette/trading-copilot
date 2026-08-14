using System.Text;

namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The R-2 <b>fuzzy fallback</b> for cross-source news dedup — <c>title similarity + published-time window + ticker
/// overlap</c> — used <b>only when canonical-URL matching (<see cref="NewsDedupKey"/>) finds nothing</b> (gh#764).
/// Finnhub and Tiingo rarely agree on a canonical link, so the same story routinely arrives under two different URLs;
/// without this fallback it lands as two <c>NewsRecord</c> rows and scores as two pieces of corroborating news.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precision over recall, by design.</b> The acceptance direction that matters is the false <i>positive</i>: a
/// wrongly-merged row destroys a real, distinct signal, which is worse than a duplicate that merely over-counts. So
/// all <b>three</b> signals must hold (a conjunction, mirroring R-2's own <c>+</c> list), and the thresholds are set
/// where syndicated near-duplicates clear them comfortably while two distinct stories about the same ticker on the
/// same day do not:
/// </para>
/// <list type="bullet">
/// <item><description><b>Title similarity ≥ <see cref="MinTitleSimilarity"/></b> — the Sørensen–Dice coefficient over
/// the two titles' normalized word sets (order-independent, robust to punctuation, casing and a differing source
/// suffix). Two providers' headlines for one story overlap heavily (typically ≥ 0.7); two distinct same-ticker
/// stories share little more than the company name (typically ≤ 0.3), so 0.6 sits in the gap with margin —
/// especially guarded by the two conditions below.</description></item>
/// <item><description><b>Published within <see cref="MaxPublishedGap"/></b> — two feeds stamp the same story minutes
/// apart, not hours; an hour is generous enough for syndication lag yet far tighter than a trading session.</description></item>
/// <item><description><b>At least one shared ticker</b> (case-insensitive) — the same story is tagged with the same
/// instrument by both providers. If either item carries no ticker there is no overlap to establish, so the fallback
/// does <b>not</b> fire (it never merges on title + time alone).</description></item>
/// </list>
/// <para>
/// Dependency-free and pure, so it is trivially unit-testable and reaches no store; the caller (<c>NewsIngestionService</c>)
/// applies it across the pass's items and the recently-stored rows. The live tuning against real provider feeds is
/// gh#464's staging proof; these are the engine defaults.
/// </para>
/// </remarks>
public static class NewsFuzzyDedup
{
    /// <summary>The minimum title Sørensen–Dice similarity (0–1) for two items to be the same story (gh#764).</summary>
    public const double MinTitleSimilarity = 0.6;

    /// <summary>The widest publication-time gap two feeds may stamp the same story with (gh#764).</summary>
    public static readonly TimeSpan MaxPublishedGap = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Whether two news items are <b>likely the same story</b> under the R-2 fuzzy fallback — title similarity AND a
    /// tight publication-time window AND at least one shared ticker, all three (gh#764).
    /// </summary>
    /// <param name="titleA">The first item's headline.</param>
    /// <param name="publishedA">The first item's publication time.</param>
    /// <param name="tickersA">The first item's tagged tickers.</param>
    /// <param name="titleB">The second item's headline.</param>
    /// <param name="publishedB">The second item's publication time.</param>
    /// <param name="tickersB">The second item's tagged tickers.</param>
    /// <returns><see langword="true"/> when all three signals agree the items are the same story.</returns>
    public static bool AreLikelyTheSameStory(
        string titleA,
        DateTimeOffset publishedA,
        IReadOnlyCollection<string> tickersA,
        string titleB,
        DateTimeOffset publishedB,
        IReadOnlyCollection<string> tickersB)
    {
        ArgumentNullException.ThrowIfNull(tickersA);
        ArgumentNullException.ThrowIfNull(tickersB);

        // Cheapest, most-selective checks first; title similarity (the only allocation) is last.
        if ((publishedA - publishedB).Duration() > MaxPublishedGap)
        {
            return false;
        }

        if (!SharesTicker(tickersA, tickersB))
        {
            return false;
        }

        return TitleSimilarity(titleA, titleB) >= MinTitleSimilarity;
    }

    /// <summary>
    /// The Sørensen–Dice similarity of two titles over their normalized word sets: <c>2·|A∩B| / (|A|+|B|)</c>, in
    /// <c>[0, 1]</c>. Case-, punctuation- and word-order-insensitive; <c>0</c> when either title has no words.
    /// </summary>
    /// <param name="titleA">The first title.</param>
    /// <param name="titleB">The second title.</param>
    /// <returns>The similarity in <c>[0, 1]</c>.</returns>
    public static double TitleSimilarity(string titleA, string titleB)
    {
        HashSet<string> a = Tokenize(titleA);
        HashSet<string> b = Tokenize(titleB);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        int intersection = a.Count(b.Contains);
        return 2d * intersection / (a.Count + b.Count);
    }

    private static bool SharesTicker(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        HashSet<string> set = new(a, StringComparer.OrdinalIgnoreCase);
        return b.Any(set.Contains);
    }

    // The title's distinct words, lower-cased, split on every non-alphanumeric run -- so "Fed: rates held (again)"
    // and "fed rates held again!" yield the same set. Ordinal after lower-casing: the case fold already happened.
    private static HashSet<string> Tokenize(string title)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title))
        {
            return tokens;
        }

        StringBuilder word = new();
        foreach (char character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
        }

        return tokens;
    }
}
