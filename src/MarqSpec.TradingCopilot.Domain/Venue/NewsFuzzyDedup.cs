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
/// wrongly-merged row destroys a real, distinct signal, which is worse than a duplicate that merely over-counts. All
/// <b>three</b> R-2 signals must hold (a conjunction), and — because for <b>macro</b> news the ticker and time
/// conjuncts are satisfied <i>by construction</i> (every macro story is tagged to the same index ticker and released
/// in clusters), leaving the title as the only real discriminator (#827 review) — the title test is deliberately
/// stronger than a bare word-overlap score:
/// </para>
/// <list type="bullet">
/// <item><description><b>Content tokenization.</b> Titles are lower-cased, split on every non-alphanumeric run, and
/// stripped of <b>single-character</b> tokens (so <c>U.S.</c> no longer contributes junk <c>u</c>/<c>s</c> tokens to
/// every American-headline pair) and common <b>stopwords</b>, leaving the <i>content</i> words.</description></item>
/// <item><description><b>Title similarity ≥ <see cref="MinTitleSimilarity"/></b> — the Sørensen–Dice coefficient over
/// those content-token sets (order-, punctuation- and case-insensitive) — <b>and</b>
/// <b>no divergent content</b>: one title's content set must be a <b>subset</b> of the other's. This is the crux.
/// Bag-of-words similarity alone cannot tell a distinct story from a true duplicate when they differ by a single
/// content word, because that case is lexically identical to a lightly-reworded re-headline — and worse, the false
/// pair often scores <i>higher</i> (<c>"Fed holds rates steady…"</c> vs <c>"ECB holds rates steady…"</c> ≈ 0.89, above
/// a genuine near-duplicate), so no threshold separates them. What does separate them is <b>where the distinguishing
/// words fall</b>: two <i>different</i> stories each assert content the other lacks (Fed↔ECB, Powell↔Williams,
/// jobless-claims↔retail-sales — content unique to <i>both</i> sides), whereas a cross-provider <i>duplicate</i> is
/// one headline being a fuller version of the other (its content a superset). So a merge is admitted only when one
/// side carries <b>no</b> content the other lacks.</description></item>
/// <item><description><b>Published within <see cref="MaxPublishedGap"/></b> — two feeds stamp the same story minutes
/// apart, not hours; an hour is generous enough for syndication lag yet far tighter than a trading session.</description></item>
/// <item><description><b>At least one shared ticker</b> (case-insensitive) — the same story is tagged with the same
/// instrument by both providers. If either item carries no ticker there is no overlap to establish, so the fallback
/// does <b>not</b> fire (it never merges on title + time alone).</description></item>
/// </list>
/// <para>
/// <b>The trade-off is intentional.</b> A duplicate re-headlined on <i>both</i> sides (each rewording the other's
/// words rather than one extending the other) is <b>not</b> merged and stays two rows — an over-count, the safe
/// direction, and the pre-existing behaviour. The definitive same-story judgement across arbitrary rewordings is a
/// <b>semantic</b> one, deferred to the pgvector embedding path (gh#377); this lexical rule is the deterministic
/// engine half, with live tuning against real provider feeds gh#464's staging proof.
/// </para>
/// <para>
/// Dependency-free and pure, so it is trivially unit-testable and reaches no store; the caller
/// (<c>NewsIngestionService</c>) applies it across the pass's items and the recently-stored rows.
/// </para>
/// </remarks>
public static class NewsFuzzyDedup
{
    /// <summary>The minimum title Sørensen–Dice similarity (0–1) for two items to be the same story (gh#764).</summary>
    public const double MinTitleSimilarity = 0.6;

    /// <summary>The widest publication-time gap two feeds may stamp the same story with (gh#764).</summary>
    public static readonly TimeSpan MaxPublishedGap = TimeSpan.FromMinutes(60);

    // Common English function words dropped before comparing, so a difference in a stopword alone never blocks a
    // merge and stopword overlap never inflates similarity. Deliberately only function words -- never content, never
    // a ticker-bearing noun -- so the set can grow without ever making a DISTINCT story look the same (#827 review).
    private static readonly HashSet<string> _stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "as", "at", "by", "for", "from", "in", "into", "of", "on", "onto",
        "to", "with", "amid", "after", "before", "over", "under", "up", "down", "out", "off", "than", "then",
        "this", "that", "these", "those", "it", "its", "is", "are", "was", "were", "be", "been", "being", "has",
        "have", "had", "will", "would", "can", "could", "may", "might", "more", "most", "no", "not", "new",
    };

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

        HashSet<string> a = Tokenize(titleA);
        HashSet<string> b = Tokenize(titleB);

        // Enough overlap AND no divergent content: one content set must be a subset of the other. Two DIFFERENT
        // stories each carry a content word the other lacks (Fed↔ECB, jobless-claims↔retail-sales); a cross-provider
        // DUPLICATE is one headline being a fuller version of the other. This is what a bare Dice threshold cannot do
        // -- a single-content-word difference is lexically identical to a lightly-reworded true dup (#827 review).
        return Similarity(a, b) >= MinTitleSimilarity && OneContentSetContainsTheOther(a, b);
    }

    /// <summary>
    /// The Sørensen–Dice similarity of two titles over their normalized <b>content</b>-word sets (single-character
    /// tokens and stopwords dropped): <c>2·|A∩B| / (|A|+|B|)</c>, in <c>[0, 1]</c>. Case-, punctuation- and
    /// word-order-insensitive; <c>0</c> when either title has no content words.
    /// </summary>
    /// <param name="titleA">The first title.</param>
    /// <param name="titleB">The second title.</param>
    /// <returns>The similarity in <c>[0, 1]</c>.</returns>
    public static double TitleSimilarity(string titleA, string titleB) =>
        Similarity(Tokenize(titleA), Tokenize(titleB));

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        int intersection = a.Count(b.Contains);
        return 2d * intersection / (a.Count + b.Count);
    }

    // Whether one non-empty content set is wholly contained in the other -- i.e. neither title asserts content the
    // other lacks (equal sets included). Empty sets never qualify: no content is no evidence.
    private static bool OneContentSetContainsTheOther(HashSet<string> a, HashSet<string> b) =>
        a.Count > 0 && b.Count > 0 && (a.IsSubsetOf(b) || b.IsSubsetOf(a));

    private static bool SharesTicker(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        HashSet<string> set = new(a, StringComparer.OrdinalIgnoreCase);
        return b.Any(set.Contains);
    }

    // The title's distinct CONTENT words: lower-cased, split on every non-alphanumeric run -- so "Fed: rates held
    // (again)" and "fed rates held again!" yield the same set -- with single-character tokens and stopwords dropped
    // (#827 review). Single-character drop kills the "U.S." -> u/s junk that otherwise pads every American-headline
    // pair; stopword drop keeps a function-word difference from either blocking a merge or inflating similarity.
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
                AddContentWord(tokens, word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            AddContentWord(tokens, word.ToString());
        }

        return tokens;
    }

    private static void AddContentWord(HashSet<string> tokens, string word)
    {
        // Drop single-character tokens (an abbreviation's fragments -- "U.S." -> u, s) and stopwords; both are noise
        // that a same-ticker, same-time pair of DIFFERENT stories would otherwise share (#827 review).
        if (word.Length > 1 && !_stopwords.Contains(word))
        {
            tokens.Add(word);
        }
    }
}
