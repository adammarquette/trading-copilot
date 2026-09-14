namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// A dimension along which a starred item's importance generalizes to future items (gh#27, ADR-0014). The three
/// categorical axes are resolved onto every news item by the relevance pass (gh#359) plus its provenance;
/// <see cref="SemanticEmbedding"/> is now <b>active</b> (gh#853) — a news item's stored embedding scored against the
/// operator's starred items' embeddings (max cosine similarity to the nearest star, ADR-0008 / gh#852). Only
/// <b>named-entity</b> similarity (gh#377) remains deferred and, being absent, simply contributes nothing — the
/// scorer degrades to the available axes rather than failing. A <b>refusable zero</b>: <see cref="Unknown"/> is
/// never a real dimension.
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

    /// <summary>
    /// Embedding-neighbourhood similarity to the operator's starred items (gh#853, ADR-0008): the item's stored
    /// vector's max cosine similarity to any starred item's vector — the nearest single star, not the mean. An
    /// operator-relative axis supplied by the caller, so it carries no single shared <c>Value</c>.
    /// </summary>
    SemanticEmbedding = 4,
}
