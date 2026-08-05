using System.Security.Claims;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Realtime;

public class RealtimeUserIdProviderTests
{
    [Fact]
    public void ToUserId_ShouldReturnTheOwnerId_FromTheSubClaim()
    {
        Guid owner = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner.ToString())]));

        // The connection is keyed by the same owner id every HTTP request resolves, so Clients.User(owner) reaches it.
        RealtimeUserIdProvider.ToUserId(principal).Should().Be(owner.ToString());
    }

    [Fact]
    public void ToUserId_ShouldReturnNull_WhenTheIdentityDoesNotResolve()
    {
        // Fail-closed: an unauthenticated / claimless connection is grouped under no operator and gets no owner push.
        RealtimeUserIdProvider.ToUserId(null).Should().BeNull();
        RealtimeUserIdProvider.ToUserId(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
    }
}

public class AccountRealtimeNotifierTests
{
    private readonly IHubContext<RealtimeHub> _hub = A.Fake<IHubContext<RealtimeHub>>();
    private readonly IHubClients _clients = A.Fake<IHubClients>();
    private readonly IClientProxy _proxy = A.Fake<IClientProxy>();

    public AccountRealtimeNotifierTests()
    {
        A.CallTo(() => _hub.Clients).Returns(_clients);
        A.CallTo(() => _clients.User(A<string>._)).Returns(_proxy);
    }

    [Fact]
    public async Task FillRecordedAsync_ShouldPushTheFill_ToTheOwnersConnectionsOnly()
    {
        Guid owner = Guid.NewGuid();

        await new AccountRealtimeNotifier(_hub).FillRecordedAsync(
            owner, new RealtimeFill("ORD", "FILL", 5300m, 2, DateTimeOffset.UnixEpoch), CancellationToken.None);

        A.CallTo(() => _clients.User(owner.ToString())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _proxy.SendCoreAsync(RealtimeFill.ClientMethod, A<object?[]>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task OrderStateChangedAsync_ShouldPushTheState_ToTheOwnersConnectionsOnly()
    {
        Guid owner = Guid.NewGuid();

        await new AccountRealtimeNotifier(_hub).OrderStateChangedAsync(
            owner, new RealtimeOrderState("ORD", "Cancelled", DateTimeOffset.UnixEpoch), CancellationToken.None);

        A.CallTo(() => _clients.User(owner.ToString())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _proxy.SendCoreAsync(RealtimeOrderState.ClientMethod, A<object?[]>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
