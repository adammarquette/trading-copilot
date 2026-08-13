namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// A suggestion whose lifecycle state a <b>set-based</b> transition just changed, recovered so a realtime push can be
/// routed to its owner (gh#718, R-20, ADR-0021). The drift (<see cref="ISuggestionDrift"/>) and expire
/// (<see cref="ISuggestionExpiry"/>) transitions are <c>ExecuteUpdate</c>s across all owners that return only a row
/// <b>count</b> — no per-row id or owner to route a push with. This carries exactly the id and owner the per-owner
/// push (<c>Clients.User</c>, never broadcast) needs.
/// </summary>
/// <param name="SuggestionId">The transitioned suggestion.</param>
/// <param name="UserId">Its owning operator — the push routes to this owner alone (R-20).</param>
public readonly record struct SuggestionTransition(Guid SuggestionId, Guid UserId);
