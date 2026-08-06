using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Realtime;

public class SuggestionRealtimeNotifierTests
{
    private readonly IHubContext<RealtimeHub> _hub = A.Fake<IHubContext<RealtimeHub>>();
    private readonly IHubClients _clients = A.Fake<IHubClients>();
    private readonly IClientProxy _proxy = A.Fake<IClientProxy>();

    public SuggestionRealtimeNotifierTests()
    {
        A.CallTo(() => _hub.Clients).Returns(_clients);
        A.CallTo(() => _clients.User(A<string>._)).Returns(_proxy);
    }

    [Fact]
    public async Task SuggestionChangedAsync_ShouldPushTheLifecycleState_ToTheOwnersConnectionsOnly()
    {
        Guid owner = Guid.NewGuid();

        await new SuggestionRealtimeNotifier(_hub).SuggestionChangedAsync(
            owner, new RealtimeSuggestion(Guid.NewGuid(), "Active", DateTimeOffset.UnixEpoch), CancellationToken.None);

        // Per-owner routing (R-20): the push reaches this operator's group, and no one else's.
        A.CallTo(() => _clients.User(owner.ToString())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _proxy.SendCoreAsync(RealtimeSuggestion.ClientMethod, A<object?[]>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
