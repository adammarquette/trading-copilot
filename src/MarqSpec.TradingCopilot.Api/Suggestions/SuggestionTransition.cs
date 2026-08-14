namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// One suggestion a set-based lifecycle transition moved, recovered so the caller can push a <b>per-owner</b>
/// realtime signal (gh#718). The drift (<see cref="ISuggestionDrift"/>) and expiry (<see cref="ISuggestionExpiry"/>)
/// transitions are database-evaluated <c>ExecuteUpdate</c>s that return only a row count — there is no per-row id or
/// owner to route a push with — so they now recover these pairs alongside the count. The new state is implicit in
/// the transition that produced it (drift → <see cref="Domain.Suggestions.SuggestionState.Stale"/>, expiry →
/// <see cref="Domain.Suggestions.SuggestionState.ExpiredVoid"/>), so it is not carried here.
/// </summary>
/// <param name="SuggestionId">The transitioned suggestion's id — the payload the card surface upserts by (gh#654).</param>
/// <param name="UserId">The owning operator (R-20), the routing key for <c>Clients.User</c> — never a broadcast.</param>
public sealed record SuggestionTransition(Guid SuggestionId, Guid UserId);
