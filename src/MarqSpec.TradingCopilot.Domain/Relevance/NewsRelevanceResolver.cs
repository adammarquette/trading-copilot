using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Domain.Relevance;

/// <summary>
/// Resolves a news item to the trading contexts it bears on (gh#359, R-2): the deterministic, pure mapping from a
/// story's tickers + text to matched instruments and topics, given the deployment's global relevance config. No
/// clock, no store, no LLM. Personalized salience (a per-user star reweighting these matches) is a separate
/// concern — gh#27.
/// </summary>
/// <remarks>
/// Instruments come from the <b>ticker↔instrument maps</b> (SPY → ES; a ticker may map to several, and several
/// tickers may map to one) and from an <b>instrument-scoped topic</b> that matches. Topics come from keyword
/// matches over the story text. An <b>unmapped</b> ticker contributes no instrument — no misattribution; the item
/// falls through to whatever global/topic matches it has, or to none. Matching is case-insensitive, and the output
/// is <b>sorted</b> so the same story against the same config always yields the same matched set (deterministic,
/// re-runnable).
/// </remarks>
public static class NewsRelevanceResolver
{
    /// <summary>
    /// The default cosine-similarity threshold (gh#854) at or above which a news item's vector is considered to
    /// match a topic's embedding — a soft, additive signal over the keyword path. A domain constant for now;
    /// sourced from the relevance config in a follow-up.
    /// </summary>
    public const double DefaultSemanticThreshold = 0.75;

    /// <summary>Resolves a news item's relevance against the global config.</summary>
    /// <param name="tickers">The story's provider-tagged tickers.</param>
    /// <param name="text">The story text to keyword-match topics against (title + summary).</param>
    /// <param name="maps">The ticker↔instrument maps.</param>
    /// <param name="topics">The topic definitions.</param>
    /// <param name="newsVector">The news item's own embedding, for semantic topic match (gh#854); <see langword="null"/> disables the semantic path (keyword-only) — the fail-safe when the item is not yet embedded or the provider is down.</param>
    /// <param name="semanticThreshold">The minimum cosine similarity, in <c>[0, 1]</c>, for a semantic topic match.</param>
    /// <returns>The matched instruments and topics, each sorted and deduped.</returns>
    public static RelevanceMatch Resolve(
        IReadOnlyList<string> tickers,
        string text,
        IReadOnlyList<TickerInstrumentMapping> maps,
        IReadOnlyList<NewsTopicDefinition> topics,
        IReadOnlyList<float>? newsVector = null,
        double semanticThreshold = DefaultSemanticThreshold)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(topics);

        HashSet<string> instruments = new(StringComparer.OrdinalIgnoreCase);
        foreach (string ticker in tickers ?? [])
        {
            foreach (TickerInstrumentMapping map in maps)
            {
                if (string.Equals(map.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                {
                    instruments.Add(map.Instrument);
                }
            }
        }

        HashSet<string> matchedTopics = new(StringComparer.OrdinalIgnoreCase);
        string haystack = text ?? string.Empty;
        foreach (NewsTopicDefinition topic in topics)
        {
            bool matched = topic.Keywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword) && haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                || MatchesSemantically(newsVector, topic.Embedding, semanticThreshold);
            if (!matched)
            {
                continue;
            }

            matchedTopics.Add(topic.Name);

            // An instrument-scoped topic is a second path to instrument relevance, beyond the ticker maps.
            if (topic.Scope == TopicScope.Instrument && !string.IsNullOrWhiteSpace(topic.Instrument))
            {
                instruments.Add(topic.Instrument);
            }
        }

        return new RelevanceMatch(
            [.. instruments.OrderBy(instrument => instrument, StringComparer.Ordinal)],
            [.. matchedTopics.OrderBy(topic => topic, StringComparer.Ordinal)]);
    }

    // A news item matches a topic semantically when both carry a vector and their cosine similarity clears the
    // threshold. A topic with no embedding -- or a news item with no vector -- never matches here: the semantic
    // path is strictly additive to keywords and fails safe to keyword-only, so an un-embedded topic can never
    // match everything.
    private static bool MatchesSemantically(
        IReadOnlyList<float>? newsVector, IReadOnlyList<float>? topicEmbedding, double semanticThreshold)
    {
        // A non-positive threshold disables the semantic path. EmbeddingSimilarity maps every degenerate pair (empty,
        // zero-magnitude, or dimension-mismatched vectors) to 0, so a threshold of 0 under a bare `>=` test would match
        // EVERYTHING -- the fail-safe inverted. Requiring BOTH a strictly-positive threshold and a strictly-positive
        // similarity keeps an un-embedded or degenerate pair from ever matching, whatever a future config sets.
        if (newsVector is null || topicEmbedding is null || semanticThreshold <= 0.0)
        {
            return false;
        }

        double similarity = EmbeddingSimilarity.MaxCosineSimilarity(newsVector, [topicEmbedding]);
        return similarity > 0.0 && similarity >= semanticThreshold;
    }
}
