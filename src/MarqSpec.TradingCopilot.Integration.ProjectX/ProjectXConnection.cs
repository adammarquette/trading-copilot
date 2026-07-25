using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// The ProjectX adapter's <see cref="IVenueConnection"/> (R-17, gh#209): venue-neutral connection liveness over
/// the process's singleton websocket client.
/// </summary>
/// <remarks>
/// Liveness is the <b>market hub</b>'s state: a dropped market hub is what stops quotes flowing, which is exactly
/// what stalls a hidden stop's promotion — so it is the signal the orphan guard must act on. The user hub (order
/// events) is a separate concern; catastrophic protection is the native safety stop, which needs neither hub.
/// </remarks>
public sealed class ProjectXConnection : IVenueConnection
{
    private readonly IProjectXWebSocketClient _webSocket;

    /// <summary>Creates the connection view over the venue's websocket client.</summary>
    /// <param name="webSocket">The process's singleton ProjectX websocket client.</param>
    public ProjectXConnection(IProjectXWebSocketClient webSocket)
    {
        _webSocket = webSocket;
    }

    /// <inheritdoc />
    public VenueId Venue { get; } = VenueId.Parse("projectx");

    /// <inheritdoc />
    public bool IsConnected => _webSocket.MarketHubState == ConnectionState.Connected;
}
