using MarqSpec.TradingCopilot.Domain.Relevance;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A relevance topic (gh#359, R-2) — a named set of keywords with a <see cref="TopicScope"/>. <b>Deployment-global
/// config</b>, not <c>IUserOwned</c>. Matching is keyword-based for now; the embedding that would enable semantic
/// (non-keyword) topic matching is deferred with the news vector (gh#377).
/// </summary>
public sealed class NewsTopic
{
    /// <summary>The topic's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The topic's name (e.g. <c>fomc</c>) — surfaced as a matched topic on the news item.</summary>
    public required string Name { get; set; }

    /// <summary>The keywords whose presence in a story's text marks the topic matched.</summary>
    public required List<string> Keywords { get; set; }

    /// <summary>Instrument-scoped or global. Refusable zero — never <see cref="TopicScope.Unknown"/>.</summary>
    public required TopicScope Scope { get; set; }

    /// <summary>The instrument an instrument-scoped topic marks relevant; <see langword="null"/> for a global topic.</summary>
    public string? Instrument { get; set; }
}
