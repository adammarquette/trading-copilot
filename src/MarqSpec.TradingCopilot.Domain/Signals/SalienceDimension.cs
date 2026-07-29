namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// A dimension along which a starred item's importance generalizes to future items (gh#27, ADR-0014). The three
/// that exist today are resolved onto every news item by the relevance pass (gh#359) plus its provenance;
/// <b>named-entity</b> and <b>semantic-embedding</b> similarity (ADR-0008 / gh#377) are deferred and, being absent,
/// simply contribute nothing — the scorer degrades to these three rather than failing. A <b>refusable zero</b>:
/// <see cref="Unknown"/> is never a real dimension.
/// </summary>
public enum SalienceDimension
{
    /// <summary>Unset — never a real dimension.</summary>
    Unknown = 0,

    /// <summary>The traded instrument the item bears on (gh#359 <c>MatchedInstruments</c>).</summary>
    Instrument = 1,

    /// <summary>The topic the item matched (gh#359 <c>MatchedTopics</c>).</summary>
    Topic = 2,

    /// <summary>The source feed that carried the item (provenance — a weaker similarity signal).</summary>
    Source = 3,
}
