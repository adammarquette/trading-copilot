using System.Text;

namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The R-2 <b>fuzzy fallback</b> for cross-source news dedup — <c>title similarity + published-time window + ticker
/// overlap</c> — used <b>only when canonical-URL matching (<see cref="NewsDedupKey"/>) finds nothing</b> (gh#764).
/// Finnhub and Tiingo rarely agree on a canonical link, so the same story routinely arrives under two different URLs;
/// without this fallback it lands as two <c>NewsRecord</c> rows and scores as two pieces of corroborating news. The
/// lexical rule catches a <b>truncated / extended</b> re-headline (one title's content a superset of the other's); an
/// <b>arbitrary rewording</b> — each side rephrasing the other — stays two rows, the safe over-count, pending the
/// semantic <c>pgvector</c> path (gh#377). See the trade-off paragraph below.
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
/// stripped of common <b>grammatical</b> stopwords, leaving the <i>content</i> words. Dotted/ampersand abbreviations
/// stay whole (<c>U.S.</c> → <c>us</c>, <c>S&amp;P</c> → <c>sp</c>) rather than becoming punctuation fragments; single
/// characters are retained when they are the only entity distinction.</description></item>
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
/// side carries <b>no</b> content the other lacks. <b>Polarity, direction, time-order and modality are the exception
/// to \"fuller\"</b>: their sets must match exactly, so <c>will not restate</c> never merges with <c>will restate</c>,
/// nor <c>may cut</c> with <c>cut</c>.</description></item>
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

    // Common English grammatical glue dropped before comparing, so its overlap never inflates similarity. This list
    // EXCLUDES polarity, direction, time-order and modality: "not"/"no", "up"/"down", "before"/"after" and
    // "will"/"may"/"might"/etc. are semantic claims, not noise -- dropping one would make an opposite headline look
    // identical and violate the precision-first invariant (#827 review).
    private static readonly HashSet<string> _stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "as", "at", "by", "for", "from", "in", "into", "of", "on", "onto",
        "to", "with", "amid", "over", "under", "out", "off", "than", "then",
        "this", "that", "these", "those", "it", "its", "is", "are", "was", "were", "be", "been", "being", "has",
        "have", "had",
    };

    // A small, explicit subset of content tokens whose PRESENCE is itself a claim. The ordinary subset rule admits a
    // fuller headline (A's content is contained in B's); that is correct for descriptive detail, but WRONG for
    // negation/modality/direction: "will not restate" must never be treated as a fuller spelling of "will restate".
    // Keep this list deliberately narrow and semantic -- it is a guard against false positives, not a second stoplist.
    private static readonly HashSet<string> _semanticModifiers = new(StringComparer.Ordinal)
    {
        "no", "not", "never", "without",
        "up", "down", "higher", "lower", "rise", "rises", "rising", "fell", "falls", "falling",
        "before", "after",
        "will", "would", "can", "could", "may", "might", "must", "should", "shall",
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
    /// <param name="minTitleSimilarity">
    /// The similarity floor, defaulting to <see cref="MinTitleSimilarity"/>. Supplied by the caller so the knob can
    /// be tuned from configuration (gh#836) without this class taking a dependency — it stays pure and
    /// dependency-free, which is what makes it trivially unit-testable.
    /// </param>
    /// <param name="maxPublishedGap">The gap ceiling, defaulting to <see cref="MaxPublishedGap"/>.</param>
    /// <returns><see langword="true"/> when all three signals agree the items are the same story.</returns>
    public static bool AreLikelyTheSameStory(
        string titleA,
        DateTimeOffset publishedA,
        IReadOnlyCollection<string> tickersA,
        string titleB,
        DateTimeOffset publishedB,
        IReadOnlyCollection<string> tickersB,
        double minTitleSimilarity = MinTitleSimilarity,
        TimeSpan? maxPublishedGap = null)
    {
        ArgumentNullException.ThrowIfNull(tickersA);
        ArgumentNullException.ThrowIfNull(tickersB);

        // Cheapest, most-selective checks first; title similarity (the only allocation) is last.
        if ((publishedA - publishedB).Duration() > (maxPublishedGap ?? MaxPublishedGap))
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
        // One exception: polarity/modality/direction words are claims, not descriptive detail, so they must match
        // exactly; otherwise "will not restate" would be admitted as a fuller form of "will restate".
        return Similarity(a, b) >= minTitleSimilarity
            && OneContentSetContainsTheOther(a, b)
            && SemanticModifiersMatch(a, b);
    }

    /// <summary>
    /// The Sørensen–Dice similarity of two titles over their normalized <b>content</b>-word sets (grammatical
    /// stopwords dropped; dotted/ampersand abbreviations normalized, semantic single-character tokens retained):
    /// <c>2·|A∩B| / (|A|+|B|)</c>, in <c>[0, 1]</c>. Case-, punctuation- and word-order-insensitive; <c>0</c>
    /// when either title has no content words.
    /// </summary>
    /// <param name="titleA">The first title.</param>
    /// <param name="titleB">The second title.</param>
    /// <returns>The similarity in <c>[0, 1]</c>.</returns>
    public static double TitleSimilarity(string titleA, string titleB) =>
        Similarity(Tokenize(titleA), Tokenize(titleB));

    /// <summary>
    /// Whether <paramref name="candidate"/> is a <b>strictly fuller</b> rendering of <paramref name="current"/> — its
    /// content tokens a proper superset of the current title's — so a fuzzy merge can adopt it without losing any
    /// content the survivor carried (the fix for "first feed wins" keeping the poorer headline, #827 review).
    /// </summary>
    /// <param name="current">The survivor's current title.</param>
    /// <param name="candidate">The incoming title offered as a fuller rendering.</param>
    /// <returns><see langword="true"/> when the candidate strictly contains the current content and adds to it.</returns>
    public static bool IsFullerRendering(string current, string candidate)
    {
        HashSet<string> currentTokens = Tokenize(current);
        return currentTokens.Count > 0 && currentTokens.IsProperSubsetOf(Tokenize(candidate));
    }

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

    private static bool SemanticModifiersMatch(HashSet<string> a, HashSet<string> b) =>
        a.Where(_semanticModifiers.Contains).ToHashSet(StringComparer.Ordinal)
            .SetEquals(b.Where(_semanticModifiers.Contains));

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
    // (again)" and "fed rates held again!" yield the same set. Dotted/ampersand abbreviations are kept whole:
    // "U.S." -> "us", "U.K." -> "uk", "S&P" -> "sp", rather than losing their entity meaning to punctuation
    // fragments (#827 review). Grammatical stopwords drop; polarity/direction/time/modality stay as content.
    private static HashSet<string> Tokenize(string title)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title))
        {
            return tokens;
        }

        title = ExpandContractions(title);

        StringBuilder word = new();
        for (int index = 0; index < title.Length; index++)
        {
            char character = title[index];
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (IsAbbreviationJoin(character, word, title, index))
            {
                // "U.S." / "S&P": join a single-letter abbreviation segment to the next letter rather than
                // flushing it as separate junk tokens. Whitespace around '&' (ordinary "A & B") fails this guard.
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
        // Do NOT drop all single-character tokens: an un-dotted "X" can be the only entity distinction between two
        // otherwise-template headlines. Dotted/ampersand forms are normalized above; grammatical glue alone drops.
        if (!_stopwords.Contains(word))
        {
            tokens.Add(word);
        }
    }

    private static bool IsAbbreviationJoin(char separator, StringBuilder word, string title, int index) =>
        separator is '.' or '&'
        && word.Length == 1
        && index + 1 < title.Length
        && char.IsLetterOrDigit(title[index + 1]);

    // Expands contracted negation/modality so the polarity guard sees it: "won't" split on the apostrophe to
    // {won, t}, and neither token is "not", so a contracted negative slipped past _semanticModifiers and merged
    // "Fed won't cut" with "Fed cut" (#827 review). Providers vary between the straight (') and curly (’) apostrophe,
    // so normalize both. The irregular stems (won't -> will not, can't -> can not, shan't -> shall not) run before the
    // general "<stem>n't" -> "<stem> not" so they are not mangled to "wo not" / "ca not".
    private static string ExpandContractions(string title)
    {
        string expanded = title.ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');
        expanded = expanded
            .Replace("won't", "will not", StringComparison.Ordinal)
            .Replace("can't", "can not", StringComparison.Ordinal)
            .Replace("shan't", "shall not", StringComparison.Ordinal)
            .Replace("ain't", "is not", StringComparison.Ordinal);
        return expanded.Replace("n't", " not", StringComparison.Ordinal);
    }
}
