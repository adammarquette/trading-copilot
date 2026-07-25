using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The orphan guard (gh#209, ADR-0007/0013): on a venue-connection loss, hidden working stops can no longer
/// promote, so they are orphaned; on reconnect they re-arm. The safety-relevant behaviours: only <c>Hidden</c>
/// stops orphan (a native one is exchange-held and untouched), the transition is one-directional per event, and
/// the guard acts across the R-20 boundary (background plumbing) while preserving each stop's owner.
/// </summary>
public class OrphanGuardServiceTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(user ?? Guid.NewGuid()));
    }

    private OrphanGuardService Service() => new(Context(), NullLogger<OrphanGuardService>.Instance);

    private async Task SeedStopAsync(StopStaging staging, Guid? owner = null)
    {
        await using TradingCopilotDbContext context = Context();
        context.StopPlans.Add(new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner ?? Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Side = OrderSide.Buy,
            EntryPrice = 5_000m,
            ActualStopPrice = 4_995m,
            SafetyStopPrice = 4_990m,
            ProximityMetric = StopProximityMetric.Ticks,
            ProximityValue = 8,
            Staging = staging,
        });
        await context.SaveChangesAsync();
    }

    private async Task<List<StopPlanRecord>> AllStopsAsync()
    {
        await using TradingCopilotDbContext reload = Context();
        return await reload.StopPlans.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public async Task OrphanAsync_ShouldMoveOnlyHiddenStopsToOrphaned_LeavingNativeUntouched()
    {
        await SeedStopAsync(StopStaging.Hidden);
        await SeedStopAsync(StopStaging.Hidden);
        await SeedStopAsync(StopStaging.Native); // exchange-held -- not synthetic, not at risk from a drop

        int orphaned = await Service().OrphanAsync(CancellationToken.None);

        orphaned.Should().Be(2);
        List<StopPlanRecord> stops = await AllStopsAsync();
        stops.Count(s => s.Staging == StopStaging.Orphaned).Should().Be(2);
        stops.Count(s => s.Staging == StopStaging.Native).Should().Be(1); // the native stop is left alone
        stops.Should().NotContain(s => s.Staging == StopStaging.Hidden);
    }

    [Fact]
    public async Task RearmAsync_ShouldMoveOrphanedStopsBackToHidden_SoPromotionResumes()
    {
        await SeedStopAsync(StopStaging.Orphaned);
        await SeedStopAsync(StopStaging.Orphaned);
        await SeedStopAsync(StopStaging.Native);

        int rearmed = await Service().RearmAsync(CancellationToken.None);

        rearmed.Should().Be(2);
        List<StopPlanRecord> stops = await AllStopsAsync();
        stops.Count(s => s.Staging == StopStaging.Hidden).Should().Be(2);
        stops.Should().NotContain(s => s.Staging == StopStaging.Orphaned);
    }

    [Fact]
    public async Task OrphanAsync_ShouldDoNothing_WhenNoHiddenStops()
    {
        await SeedStopAsync(StopStaging.Native);

        int orphaned = await Service().OrphanAsync(CancellationToken.None);

        orphaned.Should().Be(0);
        (await AllStopsAsync()).Should().OnlyContain(s => s.Staging == StopStaging.Native);
    }

    [Fact]
    public async Task OrphanAsync_ShouldActAcrossTheR20Boundary_PreservingEachOwner()
    {
        // Background plumbing acts for the deployment, not a request-user, so it orphans stops it does not "own"
        // -- but never re-assigns them. (On a single-operator deployment there is one owner; the design holds.)
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();
        await SeedStopAsync(StopStaging.Hidden, owner: alice);
        await SeedStopAsync(StopStaging.Hidden, owner: bob);

        int orphaned = await Service().OrphanAsync(CancellationToken.None);

        orphaned.Should().Be(2);
        List<StopPlanRecord> stops = await AllStopsAsync();
        stops.Should().OnlyContain(s => s.Staging == StopStaging.Orphaned);
        stops.Select(s => s.UserId).Should().BeEquivalentTo([alice, bob]); // ownership preserved, not re-assigned
    }
}
