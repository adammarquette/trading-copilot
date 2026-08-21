using FakeItEasy;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The Tradovate <see cref="IVenueConnection"/> adapter (gh#977 / gh#209): venue liveness reflects the
/// <b>market-data socket</b> state — the socket whose drop stops quotes flowing and so stalls a hidden stop's
/// promotion. Only <c>Connected</c> is live; every transient state reads as down, so the orphan guard fails safe.
/// </summary>
public class TradovateConnectionTests
{
    [Theory]
    [InlineData(ClientModels.ConnectionState.Connected, true)]
    [InlineData(ClientModels.ConnectionState.Disconnected, false)]
    [InlineData(ClientModels.ConnectionState.Connecting, false)]
    [InlineData(ClientModels.ConnectionState.Reconnecting, false)]
    public void IsConnected_ShouldBeLiveOnlyWhenTheMarketDataSocketIsConnected(ClientModels.ConnectionState state, bool expected)
    {
        ITradovateWebSocketClient client = A.Fake<ITradovateWebSocketClient>();
        A.CallTo(() => client.MarketDataState).Returns(state);

        TradovateConnection connection = new(client);

        connection.IsConnected.Should().Be(expected);
    }

    [Fact]
    public void Venue_ShouldTagTradovate()
    {
        TradovateConnection connection = new(A.Fake<ITradovateWebSocketClient>());

        connection.Venue.Should().Be(VenueId.Parse("tradovate"));
    }
}
