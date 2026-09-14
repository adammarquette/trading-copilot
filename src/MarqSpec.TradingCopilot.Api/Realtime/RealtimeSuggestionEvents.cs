using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// A suggestion's new lifecycle state, pushed to the owning operator over the realtime hub (gh#684, R-4). A
/// <c>Suggestion</c> is <b>operator-owned</b> (R-20) and is mutated in place rather than appended to the global event
/// log, so it is routed with <c>Clients.User</c> — never broadcast. Presentation-only: the authoritative transition
/// already committed to the database. The payload is deliberately compact — the id, the new state, and when — so the
/// card surface (gh#654) upserts by id: an id it does not know, now <c>Active</c>, is a fetch-and-add; a known id now
/// terminal is a clear. The wire is a signal to reconcile, never the source of truth (the REST read model is).
/// </summary>
public sealed record RealtimeSuggestion(Guid SuggestionId, string State, DateTimeOffset At)
{
    /// <summary>The client method the hub invokes to deliver a suggestion lifecycle change.</summary>
    public const string ClientMethod = "realtimeSuggestion";
}

/// <summary>
/// Pushes an operator's own suggestion lifecycle changes to their realtime connections (gh#684). <b>Read-side
/// only</b>: it is called AFTER the suggestion write commits, and its failure never affects that write. Routing is
/// per-owner via <c>Clients.User(ownerId)</c> — suggestions are operator-owned (R-20), never broadcast — which
/// resolves through <see cref="RealtimeUserIdProvider"/> (the connection's <c>sub</c> claim). The sibling of
/// <see cref="IAccountRealtimeNotifier"/> for the suggestion lifecycle.
/// </summary>
public interface ISuggestionRealtimeNotifier
{
    /// <summary>Pushes a suggestion's new lifecycle state to the owning operator's connections.</summary>
    Task SuggestionChangedAsync(Guid ownerId, RealtimeSuggestion suggestion, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class SuggestionRealtimeNotifier : ISuggestionRealtimeNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;

    /// <summary>Creates the notifier over the hub context it pushes through.</summary>
    public SuggestionRealtimeNotifier(IHubContext<RealtimeHub> hub) => _hub = hub;

    /// <inheritdoc />
    public Task SuggestionChangedAsync(Guid ownerId, RealtimeSuggestion suggestion, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeSuggestion.ClientMethod, suggestion, cancellationToken);
}
