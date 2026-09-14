using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The current-protection read behind the safety strip's degraded indicator (gh#222, R-11 / R-19).
/// </summary>
/// <remarks>
/// <para>
/// This exists because the realtime channel alone cannot answer "am I protected right now". The hub replays a
/// backlog only when the client presents an <c>?after=</c> cursor, and a freshly loaded page has none — so a
/// reload while protection is degraded would receive <b>no</b> orphan event and render as protected. That is the
/// dangerous direction, so the alert's ground truth is a read and the event is only the prompt to re-read.
/// </para>
/// <para>
/// R-19 rides every response: degraded is <b>never</b> "unprotected". The native safety stop stays with the
/// position as the physical floor; it is the operator's <i>tighter</i> synthetic stop that is orphaned.
/// </para>
/// </remarks>
public class ProtectionEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private async Task SeedAsync(StopStaging staging, Guid? owner = null, int count = 1)
    {
        await using TradingCopilotDbContext context = Context(owner ?? _operator);
        for (int index = 0; index < count; index++)
        {
            context.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = owner ?? _operator,
                OrderId = Guid.NewGuid(),
                Side = OrderSide.Buy,
                EntryPrice = 5_000m,
                ActualStopPrice = 4_995m,
                SafetyStopPrice = 4_990m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8,
                Staging = staging,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<ProtectionStateResponse> ReadAsync()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await ProtectionEndpoints.GetStateAsync(context, CancellationToken.None);
        return ((Ok<ProtectionStateResponse>)result).Value!;
    }

    [Fact]
    public async Task GetStateAsync_ShouldReportNotDegraded_WhenNothingIsOrphaned()
    {
        await SeedAsync(StopStaging.Hidden);

        ProtectionStateResponse state = await ReadAsync();

        state.Degraded.Should().BeFalse();
        state.OrphanedStops.Should().Be(0);
    }

    [Fact]
    public async Task GetStateAsync_ShouldReportDegraded_WhenAStopIsOrphaned()
    {
        await SeedAsync(StopStaging.Orphaned, count: 2);

        ProtectionStateResponse state = await ReadAsync();

        state.Degraded.Should().BeTrue();
        state.OrphanedStops.Should().Be(2);
    }

    [Fact]
    public async Task GetStateAsync_ShouldCountOnlyOrphanedStops_WhenOtherStagingsArePresent()
    {
        // A hidden stop is armed and a retired one is gone; counting either would report degradation that has
        // already been resolved -- a permanent alert an operator learns to ignore, which is worse than none.
        await SeedAsync(StopStaging.Orphaned);
        await SeedAsync(StopStaging.Hidden, count: 3);
        await SeedAsync(StopStaging.Retired, count: 2);

        (await ReadAsync()).OrphanedStops.Should().Be(1);
    }

    [Fact]
    public async Task GetStateAsync_ShouldSayTheNativeStopRemainsTheFloor_WhenProtectionIsDegraded()
    {
        // R-19, and the distinction the whole alert turns on: the operator's TIGHTER stop is orphaned, not their
        // protection. Presenting this as "unprotected" would be false, and would invite a panic close.
        await SeedAsync(StopStaging.Orphaned);

        (await ReadAsync()).NativeSafetyStopRemainsFloor.Should().BeTrue();
    }

    [Fact]
    public async Task GetStateAsync_ShouldCountOnlyTheCallersOwnStops_WhenAnotherOperatorHasOrphanedStops()
    {
        // R-20. The orphan guard deliberately IgnoreQueryFilters because it is background plumbing acting for the
        // deployment; a user-facing read is the opposite case and must stay inside the default-deny filter.
        await SeedAsync(StopStaging.Orphaned, owner: Guid.NewGuid(), count: 4);

        ProtectionStateResponse state = await ReadAsync();

        state.OrphanedStops.Should().Be(0);
        state.Degraded.Should().BeFalse();
    }
}
