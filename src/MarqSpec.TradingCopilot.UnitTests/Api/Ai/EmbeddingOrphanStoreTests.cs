using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The orphan store's anti-join DELETEs are relational-only (gh#109) and QA integration-tier (#914). The one branch
/// reachable without a database round-trip is the guard against a non-sweepable owner kind — it refuses before any EF
/// call, so no delete can run for a kind that has no producer to check an owner against.
/// </summary>
public class EmbeddingOrphanStoreTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static EmbeddingOrphanStore Store()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new EmbeddingOrphanStore(new TradingCopilotDbContext(options, new FixedUser(Guid.NewGuid())));
    }

    [Fact]
    public async Task DeleteOrphansAsync_ShouldThrow_ForANonSweepableOwnerKind()
    {
        // Belt-and-braces should the allow-list and this switch ever drift: a producer-less / undefined kind has no
        // anti-join, so asking to sweep it is a caller error -- and it refuses BEFORE touching the database.
        Func<Task> sweep = () => Store().DeleteOrphansAsync((EmbeddingOwnerKind)99, CancellationToken.None);

        await sweep.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
