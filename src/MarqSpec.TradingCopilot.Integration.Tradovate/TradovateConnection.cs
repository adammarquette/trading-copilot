using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The Tradovate adapter's <see cref="IVenueConnection"/> (R-17, gh#977 / gh#209): venue-neutral connection
/// liveness over the process's singleton Tradovate websocket client.
/// </summary>
/// <remarks>
/// Liveness is the <b>market-data socket</b>'s state: a dropped market-data socket is what stops quotes flowing,
/// which is exactly what stalls a hidden stop's promotion — so it is the signal the orphan guard must act on. The
/// separate trading / user socket (order events) is not this concern; catastrophic protection is the native safety
/// stop, which needs neither socket. Only <see cref="ClientModels.ConnectionState.Connected"/> is live — every
/// transient state (connecting, reconnecting, disconnected) reads as down, so the guard fails safe.
/// </remarks>
public sealed class TradovateConnection : IVenueConnection
{
    private readonly ITradovateWebSocketClient _webSocket;

    /// <summary>Creates the connection view over the venue's websocket client.</summary>
    /// <param name="webSocket">The process's singleton Tradovate websocket client.</param>
    public TradovateConnection(ITradovateWebSocketClient webSocket)
    {
        _webSocket = webSocket;
    }

    /// <inheritdoc />
    public VenueId Venue { get; } = VenueId.Parse("tradovate");

    /// <inheritdoc />
    public bool IsConnected => _webSocket.MarketDataState == ClientModels.ConnectionState.Connected;
}
