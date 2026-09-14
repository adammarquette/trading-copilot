using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Relevance;

namespace MarqSpec.TradingCopilot.Api.Relevance;

/// <summary>A ticker↔instrument map create request (gh#359).</summary>
/// <param name="Ticker">The provider ticker (e.g. <c>SPY</c>).</param>
/// <param name="Instrument">The traded instrument it maps to (e.g. <c>ES</c>).</param>
public sealed record TickerMapRequest(string Ticker, string Instrument);

/// <summary>The API view of a ticker↔instrument map (gh#359).</summary>
public sealed record TickerMapResponse(string Ticker, string Instrument);

/// <summary>A topic create / edit request (gh#359). PATCH replaces the topic whole.</summary>
/// <param name="Name">The topic name (e.g. <c>fomc</c>).</param>
/// <param name="Keywords">The keywords that mark the topic matched.</param>
/// <param name="Scope">Instrument-scoped or global (never <see cref="TopicScope.Unknown"/>).</param>
/// <param name="Instrument">The instrument for an instrument-scoped topic; ignored for a global one.</param>
public sealed record TopicRequest(
    string Name,
    IReadOnlyList<string> Keywords,
    TopicScope Scope,
    string? Instrument);

/// <summary>The API view of a topic (gh#359).</summary>
public sealed record TopicResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Keywords,
    TopicScope Scope,
    string? Instrument)
{
    /// <summary>Projects a persisted topic into its API view.</summary>
    /// <param name="topic">The persisted topic.</param>
    /// <returns>The response.</returns>
    public static TopicResponse From(NewsTopic topic) =>
        new(topic.Id, topic.Name, topic.Keywords, topic.Scope, topic.Instrument);
}
