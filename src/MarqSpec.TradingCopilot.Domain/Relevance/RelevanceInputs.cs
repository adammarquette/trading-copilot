namespace MarqSpec.TradingCopilot.Domain.Relevance;

/// <summary>
/// One ticker↔instrument mapping (gh#359): a provider ticker (e.g. <c>SPY</c>) resolves to a traded instrument
/// (e.g. <c>ES</c>). Deployment-global config, not an operator preference — SPY tracking the S&amp;P is a market
/// fact.
/// </summary>
/// <param name="Ticker">The provider ticker as it appears on a news item.</param>
/// <param name="Instrument">The traded instrument symbol it maps to.</param>
public sealed record TickerInstrumentMapping(string Ticker, string Instrument);

/// <summary>
/// A topic definition for relevance matching (gh#359): a named set of keywords with a <see cref="TopicScope"/>.
/// </summary>
/// <param name="Name">The topic's name (e.g. <c>fomc</c>).</param>
/// <param name="Keywords">The keywords whose presence in a story's text marks the topic matched.</param>
/// <param name="Scope">Instrument-scoped or global.</param>
/// <param name="Instrument">The instrument an instrument-scoped topic marks relevant; <see langword="null"/> for a global topic.</param>
public sealed record NewsTopicDefinition(
    string Name,
    IReadOnlyList<string> Keywords,
    TopicScope Scope,
    string? Instrument);

/// <summary>The resolved relevance of a news item (gh#359): the instruments and topics it matched, each sorted and deduped.</summary>
/// <param name="Instruments">The traded instruments the item bears on.</param>
/// <param name="Topics">The topics the item matched.</param>
public sealed record RelevanceMatch(IReadOnlyList<string> Instruments, IReadOnlyList<string> Topics);
