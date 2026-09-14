using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// An order's new status, pushed to the owning operator over the realtime hub (gh#683, R-11). Order state is
/// <b>operator-owned</b> (R-20), not on the global event log, so it is routed with <c>Clients.User</c> — never
/// broadcast. Presentation-only: the authoritative transition already committed to the journal.
/// </summary>
public sealed record RealtimeOrderState(string VenueOrderKey, string Status, DateTimeOffset At)
{
    /// <summary>The client method the hub invokes to deliver an order-state change.</summary>
    public const string ClientMethod = "realtimeOrderState";
}

/// <summary>A fill recorded on the owning operator's account, pushed over the realtime hub (gh#683, R-11).</summary>
public sealed record RealtimeFill(string VenueOrderKey, string VenueFillKey, decimal Price, int Size, DateTimeOffset At)
{
    /// <summary>The client method the hub invokes to deliver a fill.</summary>
    public const string ClientMethod = "realtimeFill";
}

/// <summary>
/// Pushes an operator's own order-state changes and fills to their realtime connections (gh#683). <b>Read-side
/// only</b>: it is called AFTER the journal write commits, and its failure never affects that write. Routing is
/// per-owner via <c>Clients.User(ownerId)</c> — order/fill state is operator-owned (R-20), never broadcast — which
/// resolves through <see cref="RealtimeUserIdProvider"/> (the connection's <c>sub</c> claim).
/// </summary>
public interface IAccountRealtimeNotifier
{
    /// <summary>Pushes an order's new status to the owning operator's connections.</summary>
    Task OrderStateChangedAsync(Guid ownerId, RealtimeOrderState state, CancellationToken cancellationToken);

    /// <summary>Pushes a recorded fill to the owning operator's connections.</summary>
    Task FillRecordedAsync(Guid ownerId, RealtimeFill fill, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class AccountRealtimeNotifier : IAccountRealtimeNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;

    /// <summary>Creates the notifier over the hub context it pushes through.</summary>
    public AccountRealtimeNotifier(IHubContext<RealtimeHub> hub) => _hub = hub;

    /// <inheritdoc />
    public Task OrderStateChangedAsync(Guid ownerId, RealtimeOrderState state, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeOrderState.ClientMethod, state, cancellationToken);

    /// <inheritdoc />
    public Task FillRecordedAsync(Guid ownerId, RealtimeFill fill, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeFill.ClientMethod, fill, cancellationToken);
}
