using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Realtime;

public class ChatRealtimeNotifierTests
{
    private readonly IHubContext<RealtimeHub> _hub = A.Fake<IHubContext<RealtimeHub>>();
    private readonly IHubClients _clients = A.Fake<IHubClients>();
    private readonly IClientProxy _proxy = A.Fake<IClientProxy>();

    public ChatRealtimeNotifierTests()
    {
        A.CallTo(() => _hub.Clients).Returns(_clients);
        A.CallTo(() => _clients.User(A<string>._)).Returns(_proxy);
    }

    [Fact]
    public async Task MessageAppendedAsync_ShouldPushTheMessage_ToTheOwnersConnectionsOnly()
    {
        Guid owner = Guid.NewGuid();

        await new ChatRealtimeNotifier(_hub).MessageAppendedAsync(
            owner,
            new RealtimeChatMessage(Guid.NewGuid(), Guid.NewGuid(), 2, ChatRole.Assistant, "hi", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        // Per-owner routing (R-20): the assistant turn reaches this operator's connections, and no one else's.
        A.CallTo(() => _clients.User(owner.ToString())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _proxy.SendCoreAsync(RealtimeChatMessage.ClientMethod, A<object?[]>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ChunkAsync_ShouldPushTheTokenDelta_ToTheOwnersConnectionsOnly()
    {
        Guid owner = Guid.NewGuid();

        await new ChatRealtimeNotifier(_hub).ChunkAsync(
            owner, new RealtimeChatChunk(Guid.NewGuid(), "tok"), CancellationToken.None);

        // Per-owner routing (R-20): a streamed delta reaches this operator's connections, and no one else's.
        A.CallTo(() => _clients.User(owner.ToString())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _proxy.SendCoreAsync(RealtimeChatChunk.ClientMethod, A<object?[]>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
