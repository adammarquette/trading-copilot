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
    /// <summary>Resolves a news item's relevance against the global config.</summary>
    /// <param name="tickers">The story's provider-tagged tickers.</param>
    /// <param name="text">The story text to keyword-match topics against (title + summary).</param>
    /// <param name="maps">The ticker↔instrument maps.</param>
    /// <param name="topics">The topic definitions.</param>
    /// <returns>The matched instruments and topics, each sorted and deduped.</returns>
    public static RelevanceMatch Resolve(
        IReadOnlyList<string> tickers,
        string text,
        IReadOnlyList<TickerInstrumentMapping> maps,
        IReadOnlyList<NewsTopicDefinition> topics)
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
                !string.IsNullOrWhiteSpace(keyword) && haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
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
}
