using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The sweep's discovery of stranded position-action intents (gh#1161 / gh#722): an Open row is a strand, a
/// Resolved row is not. In-memory EF, no I/O.
/// </summary>
public class ReconcileSweepDiscoveryTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private async Task<Guid> SeedIntentAsync(PositionActionIntentStatus status)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext seed = Context();
        seed.PositionActionIntents.Add(new PositionActionIntent
        {
            Id = id,
            UserId = _operator,
            AccountId = Guid.NewGuid(),
            Action = (int)PositionActionKind.Reduce,
            VenueAccountKey = "9001",
            Instrument = "MES",
            RequestedQuantity = 3,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            Outcome = status is PositionActionIntentStatus.Resolved ? "Reduced" : null,
            ResolvedAt = status is PositionActionIntentStatus.Resolved ? DateTimeOffset.UtcNow : null,
        });
        await seed.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task DiscoverStrandedAsync_ShouldIncludeAnOpenPositionActionIntent()
    {
        Guid open = await SeedIntentAsync(PositionActionIntentStatus.Open);
        await using TradingCopilotDbContext database = Context();

        IReadOnlyList<ReconcileStrandKey> stranded =
            await ReconcileSweepHost.DiscoverStrandedAsync(database, CancellationToken.None);

        stranded.Should().ContainSingle(key =>
            key.Kind == ReconcileStrandKind.PositionActionIntent
            && key.EntityId == open
            && key.Owner == _operator);
    }

    [Fact]
    public async Task DiscoverStrandedAsync_ShouldOmitAResolvedPositionActionIntent()
    {
        await SeedIntentAsync(PositionActionIntentStatus.Resolved);
        await using TradingCopilotDbContext database = Context();

        IReadOnlyList<ReconcileStrandKey> stranded =
            await ReconcileSweepHost.DiscoverStrandedAsync(database, CancellationToken.None);

        stranded.Should().BeEmpty();
    }
}
