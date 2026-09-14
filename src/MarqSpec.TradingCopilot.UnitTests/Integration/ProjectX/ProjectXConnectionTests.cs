using FakeItEasy;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// The ProjectX <see cref="IVenueConnection"/> adapter (gh#209): venue liveness reflects the <b>market hub</b>
/// state — the hub whose drop stops quotes flowing and so stalls stop promotion. Only <c>Connected</c> is live;
/// every transient/failed state reads as down, so the orphan guard fails safe.
/// </summary>
public class ProjectXConnectionTests
{
    [Theory]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Reconnecting, false)]
    [InlineData(ConnectionState.Failed, false)]
    public void IsConnected_ShouldBeLiveOnlyWhenTheMarketHubIsConnected(ConnectionState state, bool expected)
    {
        IProjectXWebSocketClient client = A.Fake<IProjectXWebSocketClient>();
        A.CallTo(() => client.MarketHubState).Returns(state);

        ProjectXConnection connection = new(client);

        connection.IsConnected.Should().Be(expected);
    }

    [Fact]
    public void Venue_ShouldTagProjectX()
    {
        ProjectXConnection connection = new(A.Fake<IProjectXWebSocketClient>());

        connection.Venue.Should().Be(VenueId.Parse("projectx"));
    }
}
