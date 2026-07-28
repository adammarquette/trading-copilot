using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
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
        typeof(Event),       // the append-only event backbone (ADR-0001): system plumbing, not workspace rows
        typeof(EventCursor), // per-consumer-group replay cursors for the backbone (ADR-0001): system plumbing
        typeof(KillSwitchState), // the deployment-wide kill switch (gh#189): one row per process, not a workspace row

        // The clean-historical bar store (gh#302). Global by R-20's OWN rule rather than by exception: the
        // requirement draws the line explicitly — operator-owned rows carry an owning identity, while
        // "reference & market data (instruments, venues, data providers, bars/ticks/quotes, raw news) is
        // shared / global". A tenant filter here would hide the market from the operator trading it.
        typeof(BarRecord),

        // Indicator projections over those bars (gh#310). Derived market data, so global for the same R-20
        // reason: a tenant filter would hide the market from the operator trading it.
        typeof(IndicatorValueRecord),

        // The deduped news / soft-signal store of record (gh#358). Global by the SAME R-20 rule the requirement
        // names explicitly — "reference & market data ... raw news is shared / global". The per-user salience
        // that reweights it (SoftSignalFeedback, gh#27) will be operator-owned; the news items themselves are not.
        typeof(NewsRecord),

        // Embeddings follow their OWNERS (gh#109). Every owner that exists today is itself global — news, market
        // snapshots — so a tenant filter here would hide derived data from the operator whose deployment
        // produced it. This entry is deliberately scoped to that reasoning: if an operator-owned type ever
        // becomes embeddable (a private rulebook rule, R-7), the ownership question reopens and this allowlist
        // entry is the place it must be re-argued rather than silently inherited.
        typeof(EmbeddingRecord),

        // The global relevance config (gh#359): the ticker↔instrument maps, topics, and the config-version marker
        // are DEPLOYMENT config (SPY→ES is a market fact, not an operator preference), so global like the news they
        // resolve. The per-user salience that reweights the resolved matches (gh#27) is the operator-owned part.
        typeof(TickerInstrumentMap),
        typeof(NewsTopic),
        typeof(RelevanceConfigState),

        // Notifications owed to the operator (gh#400). An alert belongs to the DEPLOYMENT: the relay that
        // delivers it runs as background plumbing with no authenticated identity, so a tenant filter would hide
        // undelivered pages from the very sweep whose job is to deliver them — silently, which is precisely the
        // dropped-alert failure the outbox exists to close.
        typeof(NotificationOutboxRecord),
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
    public async Task AFirm_RoundTripsWithItsStageConventions_AndBridgesToTheDomainConventions()
    {
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid firmId = Guid.NewGuid();

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            asOwner.Firms.Add(new Firm
            {
                Id = firmId,
                UserId = owner,
                Name = "Topstep",
                Type = FirmType.PropFirm,
                StageConventions =
                [
                    new FirmStageConvention { Id = Guid.NewGuid(), UserId = owner, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
                    new FirmStageConvention { Id = Guid.NewGuid(), UserId = owner, FirmId = firmId, Stage = AccountStage.Funded, CapitalAtRisk = true },
                ],
            });
            await asOwner.SaveChangesAsync();
        }

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            Firm loaded = await asOwner.Firms.Include(f => f.StageConventions).SingleAsync();

            FirmConventions conventions = loaded.ToConventions();
            conventions.ModeFor(AccountStage.Practice).Should().Be(TradingMode.Practice);
            conventions.ModeFor(AccountStage.Funded).Should().Be(TradingMode.Live);
        }
    }

    [Fact]
    public async Task TheFirmLoginSpine_RoundTrips_AndStaysInvisibleToOtherOperators()
    {
        Guid owner = Guid.NewGuid();
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            asOwner.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
            asOwner.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = "topstep-projectx", // a NAME, never a secret -- credentials live in env
            });
            asOwner.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = "9001",
                Name = "PRAC-50K-1234",
                Stage = AccountStage.Practice,
                CanTrade = true,
                IsVisible = true,
                Balance = 50_000m,
            });
            await asOwner.SaveChangesAsync();
        }

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            Connection loaded = await asOwner.Connections.Include(c => c.Accounts).SingleAsync();
            loaded.Platform.Should().Be("projectx");
            loaded.Accounts.Should().ContainSingle(account => account.VenueAccountKey == "9001"
                && account.Stage == AccountStage.Practice);
        }

        // The login spine carries the operator's broker relationships -- exactly what R-20 exists to scope.
        await using TradingCopilotDbContext asStranger = ContextFor(Guid.NewGuid(), database);
        (await asStranger.Connections.AnyAsync()).Should().BeFalse();
        (await asStranger.Accounts.AnyAsync()).Should().BeFalse();
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
