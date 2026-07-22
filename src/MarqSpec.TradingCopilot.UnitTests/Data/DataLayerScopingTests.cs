using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// R-20 default-deny scoping — the data layer's fail-closed safety property (ADR-0017). Two things are guarded:
/// that a query which forgets its scope returns <b>nothing</b>, and that a new entity cannot slip into the model
/// without a deliberate owned-vs-global decision.
/// </summary>
public class DataLayerScopingTests
{
    /// <summary>
    /// Entities intentionally <b>not</b> operator-owned — each must be justified, because a non-owned entity has
    /// no default-deny filter and is therefore globally readable. Adding to this set is a reviewed decision.
    /// </summary>
    private static IReadOnlySet<Type> AcknowledgedGlobals { get; } = new HashSet<Type>
    {
        typeof(User),        // the tenant root: owns other rows, is not itself owned
        typeof(Invitation),  // anonymous onboarding by token hash (R-18), resolved before a user exists
    };

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext ContextFor(Guid userId, string databaseName)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(userId));
    }

    [Fact]
    public void EveryEntity_MustBeOperatorOwnedOrAnAcknowledgedGlobal()
    {
        // The guard gh#7 calls for: the auto-filter covers everything implementing IUserOwned, so the only way to
        // leak is to add an entity that *should* be owned and forget the marker. This fails if that ever happens.
        using TradingCopilotDbContext context = ContextFor(Guid.NewGuid(), Guid.NewGuid().ToString());

        IEnumerable<Type> entities = context.Model.GetEntityTypes().Select(type => type.ClrType);

        foreach (Type entity in entities)
        {
            bool owned = typeof(IUserOwned).IsAssignableFrom(entity);

            (owned || AcknowledgedGlobals.Contains(entity)).Should().BeTrue(
                $"{entity.Name} must implement IUserOwned (default-deny scoped) or be an acknowledged global — "
                + "an entity that is neither has no per-user filter and is an R-20 leak (gh#7).");
        }
    }

    [Fact]
    public async Task AnOperatorOwnedRow_IsInvisibleToADifferentOperatorsContext()
    {
        Guid operatorA = Guid.NewGuid();
        Guid operatorB = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();

        await using (TradingCopilotDbContext asA = ContextFor(operatorA, database))
        {
            asA.Firms.Add(new Firm { Id = Guid.NewGuid(), UserId = operatorA, Name = "Topstep", Type = FirmType.PropFirm });
            await asA.SaveChangesAsync();
        }

        // A forgets-its-scope-returns-nothing check: operator B queries the same table and sees none of A's rows.
        await using (TradingCopilotDbContext asB = ContextFor(operatorB, database))
        {
            (await asB.Firms.ToListAsync()).Should().BeEmpty();
        }

        // ...and the owner still sees their own, so the filter scopes rather than simply hides everything.
        await using (TradingCopilotDbContext asA = ContextFor(operatorA, database))
        {
            (await asA.Firms.ToListAsync()).Should().ContainSingle(firm => firm.Name == "Topstep");
        }
    }

    [Fact]
    public async Task WithNoUserContext_TheFilterMatchesNothing()
    {
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            asOwner.Firms.Add(new Firm { Id = Guid.NewGuid(), UserId = owner, Name = "Apex", Type = FirmType.PropFirm });
            await asOwner.SaveChangesAsync();
        }

        // Guid.Empty is "no authenticated user" (ICurrentUser). Default-deny means it reads nothing, never all.
        await using TradingCopilotDbContext anonymous = ContextFor(Guid.Empty, database);
        (await anonymous.Firms.ToListAsync()).Should().BeEmpty();
    }
}
