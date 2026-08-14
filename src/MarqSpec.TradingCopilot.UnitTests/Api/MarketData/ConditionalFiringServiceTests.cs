using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The conditional-order firing watcher's core (gh#198, ADR-0007): on a quote, fire the pending conditionals the
/// trigger crossed — through the authoritative fire-time re-gate — and cancel/expire the stale ones. The
/// safety-relevant behaviours: a fired order goes through the gate and journals its order + stop plan; a
/// gate-refused fire stays pending (never lost); a resolved order never re-fires; and nothing fires without its
/// trigger.
/// </summary>
public class ConditionalFiringServiceTests
{
    private const string Contract = "CON.F.US.MES.U26";
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ConditionalFiringServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]); // flat
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), Contract), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options, new FixedUser(_operator));

    private ConditionalFiringService Service(
        ILogger<ConditionalFiringService>? logger = null, IAccountEntryGuard? guard = null) => new(
        Context(),
        Options,
        _factory,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions()),
        new HostTradingEnvironment(DeploymentEnvironment.Development),
        A.Fake<IKillSwitch>(),
        guard ?? PassthroughGuard(),
        logger ?? NullLogger<ConditionalFiringService>.Instance);

    /// <summary>
    /// The account-entry guard (gh#589) that just runs the fire callback: the in-memory provider cannot run the real
    /// advisory lock (that is why it is a seam). The deterministic no-stacking check INSIDE the callback is what the
    /// unit tier proves; the real cross-request serialization is proven on Postgres by QA.
    /// </summary>
    private static IAccountEntryGuard PassthroughGuard()
    {
        IAccountEntryGuard guard = A.Fake<IAccountEntryGuard>();
        // The fire path takes the lock NON-BLOCKING (TryRunExclusiveAsync, gh#589). Here the account is free, so the
        // fake acquires it and just invokes the callback; the deterministic no-stacking check INSIDE it is what the
        // unit tier proves. The busy path (onBusy → stays Pending) is exercised by its own test with a busy fake.
        A.CallTo(() => guard.TryRunExclusiveAsync<ConditionalFiringService.FiringOutcome>(
                A<TradingCopilotDbContext>._, A<Guid>._,
                A<Func<Task<ConditionalFiringService.FiringOutcome>>>._,
                A<Func<ConditionalFiringService.FiringOutcome>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _,
                Func<Task<ConditionalFiringService.FiringOutcome>> transmit,
                Func<ConditionalFiringService.FiringOutcome> _, CancellationToken _) => transmit());
        return guard;
    }

    private async Task<Guid> SeedAccountAsync(string venueAccountKey = "9001")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = _operator,
            Name = "Topstep",
            Type = FirmType.PropFirm,
            StageConventions =
            [
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = _operator, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
            ],
        });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = venueAccountKey,
            Name = $"PRAC-{venueAccountKey}",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        context.RiskProfiles.Add(new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = accountId,
            StartingBalance = 50_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = 0.15m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = 600m,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = 3,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task ProcessQuote_ShouldHoldTheFire_WhenTheAccountAlreadyHasAWorkingOrder()
    {
        // gh#589: a conditional fire is a new entry, so it runs the no-stacking check under the account lock. With an
        // outstanding Working order on the account, the fire is HELD Pending (re-decide next quote) rather than
        // stacking a second entry sized against a flat snapshot -- closing the send/take-vs-conditional-fire race.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);
        await SeedWorkingOrderAsync(accountId);

        await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Pending)).Should().Be(
            1, "the fire is held pending while the account already holds an outstanding order (gh#589)");
        (await reload.Orders.CountAsync()).Should().Be(1, "no new entry is stacked -- only the pre-existing Working order");
    }

    [Fact]
    public async Task ProcessQuote_ShouldFireUnderTheAccountEntryGuard()
    {
        // gh#589: the fire's compose -> gate -> transmit -> journal must run under the per-account lock, so it
        // serializes against operator sends / takes. Pin that it goes through the guard for THIS account; the real
        // (non-blocking) advisory-lock serialization is QA's on Postgres.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        IAccountEntryGuard guard = PassthroughGuard();
        await Service(guard: guard).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        A.CallTo(() => guard.TryRunExclusiveAsync<ConditionalFiringService.FiringOutcome>(
                A<TradingCopilotDbContext>._, accountId,
                A<Func<Task<ConditionalFiringService.FiringOutcome>>>._,
                A<Func<ConditionalFiringService.FiringOutcome>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessQuote_ShouldHoldTheFire_WhenTheAccountLockIsBusy()
    {
        // gh#589 (the watcher-stall fix): the fire takes the lock NON-BLOCKING. If an operator send / take holds the
        // account lock, the fire must NOT wait -- waiting would stall the single watcher for EVERY account. It leaves
        // the conditional Pending and re-decides next quote. Here the guard reports busy (runs onBusy, never the
        // callback), so nothing is transmitted and the conditional stays Pending.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        IAccountEntryGuard busy = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => busy.TryRunExclusiveAsync<ConditionalFiringService.FiringOutcome>(
                A<TradingCopilotDbContext>._, A<Guid>._,
                A<Func<Task<ConditionalFiringService.FiringOutcome>>>._,
                A<Func<ConditionalFiringService.FiringOutcome>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _,
                Func<Task<ConditionalFiringService.FiringOutcome>> _,
                Func<ConditionalFiringService.FiringOutcome> onBusy, CancellationToken _) => onBusy());

        await Service(guard: busy).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Pending)).Should().Be(
            1, "a busy account lock leaves the fire Pending to re-decide next quote (gh#589), never blocking the watcher");
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFire_WhenTheConditionalWasWithdrawnInThePreLockWindow()
    {
        // gh#655 (review): the operator withdrawal (DELETE /conditionals/{id}) is a SECOND writer that moves a
        // conditional out of Pending, committing under THIS same account lock. The watcher's Pending read happens
        // BEFORE it takes the lock, so a withdrawal that commits in that pre-lock window is not yet reflected in the
        // record the fire holds. Without a re-read under the lock, `Status = Firing` would OVERWRITE the withdrawn
        // `Cancelled` and place a real order after a 200 "withdrawn". FireAsync must reload under the lock and bail.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310, Pending

        // Model the race: at the instant the fire acquires the lock (the callback runs), the operator's withdrawal
        // has just committed the row to Cancelled in a separate unit of work — exactly the pre-lock-window interleave.
        IAccountEntryGuard racingGuard = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => racingGuard.TryRunExclusiveAsync<ConditionalFiringService.FiringOutcome>(
                A<TradingCopilotDbContext>._, A<Guid>._,
                A<Func<Task<ConditionalFiringService.FiringOutcome>>>._,
                A<Func<ConditionalFiringService.FiringOutcome>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _,
                Func<Task<ConditionalFiringService.FiringOutcome>> transmit,
                Func<ConditionalFiringService.FiringOutcome> _, CancellationToken _) =>
                WithdrawThenTransmitAsync(conditionalId, transmit));

        await Service(guard: racingGuard).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(
            ConditionalStatus.Cancelled, "the withdrawal stands; the fire re-read under the lock and bailed");
        (await reload.Orders.AnyAsync()).Should().BeFalse(
            "no order is placed on a conditional withdrawn before the fire's locked section");
    }

    /// <summary>
    /// Simulates the operator's withdrawal committing at the instant the fire takes the account lock: flips the row to
    /// <see cref="ConditionalStatus.Cancelled"/> in a separate unit of work, then runs the fire callback. Returns the
    /// fire's outcome so the guard fake stays typed to <c>Task&lt;FiringOutcome&gt;</c>.
    /// </summary>
    private async Task<ConditionalFiringService.FiringOutcome> WithdrawThenTransmitAsync(
        Guid conditionalId, Func<Task<ConditionalFiringService.FiringOutcome>> transmit)
    {
        await using (TradingCopilotDbContext withdraw = Context())
        {
            ConditionalOrderRecord row = await withdraw.ConditionalOrders.SingleAsync(c => c.Id == conditionalId);
            row.Status = ConditionalStatus.Cancelled;
            await withdraw.SaveChangesAsync();
        }

        return await transmit();
    }

    private async Task SeedWorkingOrderAsync(Guid accountId)
    {
        await using TradingCopilotDbContext seed = Context();
        seed.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = accountId,
            Instrument = Contract,
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            VenueOrderKey = "999999",
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        await seed.SaveChangesAsync();
    }

    private async Task<Guid> AddConditionalAsync(
        Guid accountId,
        ConditionalCrossDirection direction = ConditionalCrossDirection.RisesTo,
        decimal trigger = 5310m,
        decimal safetyStop = 5290m,
        int size = 1,
        decimal? cancelDrift = null,
        DateTimeOffset? expiresAt = null,
        ConditionalStatus status = ConditionalStatus.Pending)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = id,
            UserId = _operator,
            AccountId = accountId,
            Instrument = Contract,
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = size,
            Type = OrderType.Market,
            EntryPrice = 5300m,
            WorkingStopPrice = 5295m,
            SafetyStopPrice = safetyStop,
            ReferencePrice = 5300m,
            TickSize = 0.25m,
            PointValue = 5m,
            TriggerPrice = trigger,
            TriggerDirection = direction,
            CancelDriftPrice = cancelDrift,
            ExpiresAt = expiresAt,
            Status = status,
            Mode = TradingMode.Practice,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ProcessQuote_ShouldFireThroughTheGate_AndJournalTheOrderAndStopPlan_WhenTheTriggerCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(ConditionalStatus.Fired);
        conditional.FiredOrderId.Should().NotBeNull();

        Order journaled = await reload.Orders.SingleAsync();
        journaled.Id.Should().Be(conditional.FiredOrderId!.Value);
        journaled.Status.Should().Be(OrderStatus.Working);
        journaled.VenueOrderKey.Should().Be("889001");
        journaled.UserId.Should().Be(_operator);                        // ownership preserved on the journaled row
        journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional); // placed by the on-trigger watcher (gh#181)
        (await reload.StopPlans.CountAsync()).Should().Be(1);           // the fired position is protected (stop staged)
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the fire-time decision is audited
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFire_WhenTheTriggerHasNotCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
    }

    [Fact]
    public async Task ProcessQuote_ShouldCancel_OnAnAdverseDriftPastTheBand()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, cancelDrift: 5290m); // RisesTo 5310; stale below 5290

        // Ask 5290 is at the band and below the 5310 trigger: a drift-cancel, not a fire.
        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5289m, ask: 5290m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Cancelled);
    }

    [Fact]
    public async Task ProcessQuote_ShouldExpire_WhenTheValidityWindowHasPassed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, expiresAt: Now.AddMinutes(-1)); // already expired

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Expired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldIgnoreResolvedOrders_SoAFiredOneNeverRefires()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Fired); // already resolved

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5320m, ask: 5321m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Fired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldStayPending_WhenTheFireTimeGateRefuses()
    {
        // The trigger crossed, but at fire time the gate refuses (a $500/contract safety stop against a $300
        // budget leaves room for zero) -- the setup is not lost; it re-decides on the next quote. R-12.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, safetyStop: 5200m); // 100pt = $500/contract vs the $300 MaxDD

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
        (await reload.Orders.AnyAsync()).Should().BeFalse();            // nothing transmitted or journaled
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the blocking decision is audited
    }

    [Fact]
    public async Task ProcessQuote_ShouldRevertToPendingInsideTheAccountLock_WhenTheFireTimeGateRefuses()
    {
        // gh#630 (of #622): the gate-refusal revert (Firing -> Pending) must COMMIT INSIDE the account lock, like the
        // Firing-commit and the Fired paths -- not rely on the outer per-record save that runs AFTER the lock releases.
        // Otherwise, in the tiny unlock -> outer-save window the row still reads Firing, and a concurrent operator
        // send / take (whose no-stacking check now counts Firing, gh#589) gets a spurious 409. This observes the
        // committed status a concurrent reader would see at the instant the lock releases.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, safetyStop: 5200m); // 100pt = $500/contract vs the $300 MaxDD -> gate refuses

        ConditionalStatus? seenAtUnlock = null;
        IAccountEntryGuard observing = ObservingGuard(status => seenAtUnlock = status);

        int acted = await Service(guard: observing)
            .ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0, "a gate-refused fire transmits nothing");
        seenAtUnlock.Should().Be(
            ConditionalStatus.Pending,
            "the revert must commit inside the lock, so a concurrent send never sees a stale Firing and 409s (gh#630)");
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending); // durable end state unchanged
    }

    /// <summary>
    /// A guard that runs the fire callback and then, still "holding the lock", reads the conditional's COMMITTED status
    /// from a fresh context -- exactly what a concurrent send / take would see in the unlock -> outer-save window. The
    /// captured status is <see cref="ConditionalStatus.Firing"/> if the revert was left to the outer save (the gh#630
    /// bug), and <see cref="ConditionalStatus.Pending"/> once it commits inside the lock (the fix).
    /// </summary>
    private IAccountEntryGuard ObservingGuard(Action<ConditionalStatus?> capture)
    {
        IAccountEntryGuard guard = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => guard.TryRunExclusiveAsync<ConditionalFiringService.FiringOutcome>(
                A<TradingCopilotDbContext>._, A<Guid>._,
                A<Func<Task<ConditionalFiringService.FiringOutcome>>>._,
                A<Func<ConditionalFiringService.FiringOutcome>>._, A<CancellationToken>._))
            .ReturnsLazily(async (TradingCopilotDbContext _, Guid _,
                Func<Task<ConditionalFiringService.FiringOutcome>> transmit,
                Func<ConditionalFiringService.FiringOutcome> _, CancellationToken _) =>
            {
                ConditionalFiringService.FiringOutcome outcome = await transmit();
                await using TradingCopilotDbContext reader = Context();
                ConditionalOrderRecord? seen = await reader.ConditionalOrders.FirstOrDefaultAsync();
                capture(seen?.Status);
                return outcome;
            });
        return guard;
    }

    [Fact]
    public async Task ProcessQuote_ShouldJournalAnEarlierFire_WhenALaterConditionalOnAnotherAccountThrows()
    {
        // gh#532 — the P1, updated for gh#589. Two pending conditionals cross on the same quote, on DIFFERENT accounts
        // (both flat, so both may fire -- gh#589 serializes per ACCOUNT, not across accounts; two fires on ONE account
        // could not both reach the venue, so this property now needs two accounts to exercise). The first fires and is
        // transmitted; transmitting the second then throws (an ordinary venue rejection). Each conditional commits its
        // own owner-scoped unit of work before the next is touched, so a peer's fault can never unwind an order the
        // venue has already accepted: exactly one Order row survives, Fired and stop-protected, and the throwing
        // account's conditional is left FIRING -- a maybe-landed venue fault is never reverted to Pending (gh#589
        // review); it is a different account, so it does not block A's fire.
        Guid accountA = await SeedAccountAsync("9001");
        Guid accountB = await SeedAccountAsync("9002");
        await AddConditionalAsync(accountA);
        await AddConditionalAsync(accountB);

        // Both accounts exist at the venue (each conditional's compose matches its account key to the roster).
        VenueAccountId venueB = VenueAccountId.Create(VenueId.Parse("projectx"), "9002");
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-9001", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
            new VenueAccount(venueB, "PRAC-9002", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);

        // The venue accepts the first placement and rejects the second. A faulted Task (not a synchronous throw) is
        // exactly how the send path surfaces it: `await venue.PlaceOrderAsync(...)` observes the fault.
        int placements = 0;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                placements++;
                return placements == 2
                    ? Task.FromException<PlacedOrder>(new InvalidOperationException("venue rejected the second order (test)"))
                    : Task.FromResult(new PlacedOrder(_venueAccount, $"88900{placements}", DateTimeOffset.UnixEpoch));
            });

        // The pass may surface the second record's fault (the batched-save shape throws it out; the per-record fix
        // contains it) — either way the FIRST fire must be durably journaled, which is the property under test.
        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The later conditional's venue rejection escaping the pass — tolerated; the DB state is asserted below.
        }

        await using TradingCopilotDbContext reload = Context();
        // The safety property: an order the venue accepted is never discarded because a peer later threw. The first
        // placement succeeded, so exactly one Order row must exist, linked to a Fired conditional, protected by a stop.
        (await reload.Orders.CountAsync()).Should().Be(
            1, "the first fire's transmitted order must be journaled even though a later conditional (another account) threw");
        ConditionalOrderRecord fired = await reload.ConditionalOrders.SingleAsync(c => c.Status == ConditionalStatus.Fired);
        fired.FiredOrderId.Should().Be((await reload.Orders.SingleAsync()).Id);
        (await reload.StopPlans.CountAsync()).Should().Be(1, "the fired position's stop plan is journaled with it");
        // The conditional whose transmit threw is left FIRING, not Pending (gh#589 review): a maybe-landed venue fault
        // is indistinguishable-from-live at this seam, so it is never reverted to a re-fireable Pending.
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Firing)).Should().Be(1);
    }

    [Fact]
    public async Task ProcessQuote_ShouldStrandFiringAndBlockTheAccount_WhenTheFirstFireFaultsMaybeLive()
    {
        // gh#589 review. Three conditionals on ONE account cross on one quote; the FIRST fire's venue send FAULTS
        // (maybe-live). It is left Firing -- never reverted to Pending -- and because the no-stacking check now counts a
        // mid-fire Firing conditional, the two remaining fires are HELD Pending: the account is fail-safe BLOCKED until
        // the stranded fire is reconciled (POST /conditionals/{id}/reconcile). So NOTHING new is journaled while an
        // order may be live at the venue. The gh#532 per-record containment still holds (a fault never rolls back or
        // starves a committed peer) -- here the strand simply blocks the peers before any of them can commit.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);
        await AddConditionalAsync(accountId);
        await AddConditionalAsync(accountId);

        int placements = 0;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                // The first placement faults; no LATER fire should ever reach the venue, because the strand blocks the
                // account -- so a second placement here would itself be the defect this test guards against.
                placements++;
                return Task.FromException<PlacedOrder>(new InvalidOperationException("venue faulted the first order (test)"));
            });

        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Tolerated — the assertion is on the DB state, not on whether the (contained) fault escaped.
        }

        await using TradingCopilotDbContext reload = Context();
        placements.Should().Be(1, "only the first fire reached the venue; the strand blocks the other two BEFORE their send");
        (await reload.Orders.AnyAsync()).Should().BeFalse("nothing is journaled while a maybe-live fire is stranded (gh#589)");
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Firing)).Should().Be(
            1, "the faulted fire is left Firing -- a maybe-live entry, never reverted to a re-fireable Pending");
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Pending)).Should().Be(
            2, "the no-stacking check counts the Firing conditional, so the other two fires are held (fail-safe)");
    }

    [Fact]
    public async Task ProcessQuote_ShouldLeaveTheConditionalFiring_WhenTheVenueSendFaultsAsAMaybeLiveTimeout()
    {
        // gh#589 review (H1), the canonical case: a venue TIMEOUT surfaces as a TaskCanceledException on HttpClient's
        // OWN token -- NOT the caller's -- so it is NOT the clean-shutdown OperationCanceledException path, and must not
        // be treated as a definitive "nothing rests". The fire leaves the conditional FIRING (mirroring the take path),
        // never reverting to Pending. Reverting was a double-transmit: discovery is Pending-only so the next quote would
        // re-fire, and the no-stacking check cannot see a Pending row.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        ILogger<ConditionalFiringService> logger = A.Fake<ILogger<ConditionalFiringService>>();
        // The caller's token is never cancelled (ProcessQuoteAsync is passed CancellationToken.None), so this
        // TaskCanceledException is the maybe-landed timeout, not a clean shutdown.
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(Task.FromException<PlacedOrder>(new TaskCanceledException("venue timed out (test)")));

        try
        {
            await Service(logger: logger).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (Exception)
        {
            // Tolerated — the property under test is the DB state (Firing), not whether the fault escaped the pass.
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(
            ConditionalStatus.Firing, "a maybe-live venue send fault leaves the conditional Firing so it cannot re-fire (gh#589)");
        (await reload.Orders.AnyAsync()).Should().BeFalse("nothing was journaled -- the send faulted before the accept");
        A.CallTo(logger).Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened(); // surfaced loudly -- the operator resolves it via POST /conditionals/{id}/reconcile
    }

    [Fact]
    public async Task ProcessQuote_ShouldRevertToPending_WhenTheVenueDefinitivelyRejectsTheFire()
    {
        // gh#629: a DEFINITIVE venue rejection (VenueRefusalException, Kind == Definitive) placed NOTHING, so the fire
        // reverts the durable Firing intent to Pending -- re-decidable on the next quote (the gh#532 containment) --
        // rather than being left Firing like a maybe-live fault. It mirrors the gate-refusal revert and does not throw.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(Task.FromException<PlacedOrder>(
                new VenueRefusalException("ProjectX rejected: invalid stop", VenueRefusalKind.Definitive, 42)));

        await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(
            ConditionalStatus.Pending, "a definitive rejection placed nothing, so the fire reverts to Pending (gh#629)");
        (await reload.Orders.AnyAsync()).Should().BeFalse("nothing rests -- the venue rejected the order");
    }

    [Fact]
    public async Task ProcessQuote_ShouldRevertToPendingInsideTheAccountLock_WhenTheVenueDefinitivelyRejectsTheFire()
    {
        // gh#629 + gh#630 (of #622): the DEFINITIVE-refusal revert (Firing -> Pending) must COMMIT INSIDE the account
        // lock, exactly like the gate-refusal revert next door -- not rely on the outer per-record save that runs AFTER
        // the lock releases. Otherwise, in the unlock -> outer-save window the row still reads Firing and a concurrent
        // operator send / take (whose no-stacking check counts Firing, gh#589) gets a spurious 409. gh#630 closed this
        // window for the gate-refusal arm; this arm is new in gh#629 and must not re-open it. Same ObservingGuard as
        // ProcessQuote_ShouldRevertToPendingInsideTheAccountLock_WhenTheFireTimeGateRefuses: it reads the COMMITTED
        // status a concurrent reader would see at the instant the lock releases.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(Task.FromException<PlacedOrder>(
                new VenueRefusalException("ProjectX rejected: invalid stop", VenueRefusalKind.Definitive, 42)));

        ConditionalStatus? seenAtUnlock = null;
        IAccountEntryGuard observing = ObservingGuard(status => seenAtUnlock = status);

        await Service(guard: observing)
            .ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        seenAtUnlock.Should().Be(
            ConditionalStatus.Pending,
            "the definitive-refusal revert must commit inside the lock, so a concurrent send never sees a stale Firing "
            + "and 409s -- the gh#622 window gh#630 closed for the gate-refusal arm must not re-open here (gh#629)");
        await using TradingCopilotDbContext reloadInLock = Context();
        (await reloadInLock.ConditionalOrders.SingleAsync()).Status.Should().Be(
            ConditionalStatus.Pending, "the durable end state is unchanged -- only WHEN it commits moves");
    }

    [Fact]
    public async Task ProcessQuote_ShouldLeaveFiring_WhenTheVenueRefusalIsIndeterminate()
    {
        // gh#629 THE SAFETY CASE: an INDETERMINATE VenueRefusalException (accepted-but-no-id -- the venue MAY hold the
        // order) is maybe-live and must be left Firing, NEVER reverted to a re-fireable Pending. Mis-classifying it as
        // definitive would revert a maybe-live fire, which the next quote would re-fire -- a double-transmit.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(Task.FromException<PlacedOrder>(
                new VenueRefusalException("accepted but returned no order id", VenueRefusalKind.Indeterminate)));

        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (Exception)
        {
            // Tolerated — the property under test is the DB state (Firing), not whether the fault escaped the pass.
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(
            ConditionalStatus.Firing, "an indeterminate refusal is maybe-live -> left Firing, never reverted (gh#629 safety)");
    }

    [Fact]
    public async Task ProcessQuote_ShouldHoldTheFire_WhenTheAccountAlreadyHasAMidFireFiringConditional()
    {
        // gh#589: the no-stacking check counts a mid-fire Firing conditional, not only an outstanding Order. A Firing
        // conditional is a maybe-live entry with NO Order row (its fire faulted before journaling), so without this a
        // fresh fire on the same account would size against a flat snapshot that ignores it. Seed a stranded Firing
        // sibling directly, then a fresh conditional crosses -- it must be HELD Pending, nothing transmitted.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Firing); // a stranded sibling, mid-fire
        Guid fresh = await AddConditionalAsync(accountId); // Pending, will cross this quote

        await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse("the account holds a mid-fire Firing conditional, so the fresh fire is held (gh#589)");
        (await reload.ConditionalOrders.SingleAsync(c => c.Id == fresh)).Status.Should().Be(
            ConditionalStatus.Pending, "the fresh fire is held Pending -- it cannot stack on a maybe-live sibling fire");
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Firing)).Should().Be(
            1, "the stranded sibling is untouched -- still Firing, awaiting reconcile");
    }

    [Fact]
    public async Task ProcessQuote_ShouldTagTheVenueOrderWithTheConditionalId_SoAReplayCanRecogniseItsOwnOrder()
    {
        // gh#577 — the correlation handle. A fired conditional stamps its own id as the venue order's customTag, so an
        // order the venue accepted but a transmit→journal fault never journaled can be matched back to the conditional
        // that placed it, rather than transmitting a blind duplicate. (Manual paths carry no tag — a human is in the loop.)
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest order, CancellationToken _) => sent = order)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.CustomTag.Should().Be(
            conditionalId.ToString(), "the venue order carries the firing conditional's id as its correlation handle (gh#577)");
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotRefire_WhenAnEarlierFireWasAcceptedButItsJournalDidNotCommit()
    {
        // gh#577 — the P1 residual #532 left open. A fired conditional transmits the entry (the venue ACCEPTS it) and
        // then journals its order + stop plan on a commit that here FAILS (a DB fault / a shutdown cancellation landing
        // between the accept and the commit). Before this fix the conditional was left Pending with a live venue order
        // on no Order row, so the next crossing quote re-fired it — a duplicate live order. The durable pre-transmit
        // intent leaves it Firing instead, and discovery is Pending-only, so it can never blind-re-fire.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        // The venue accepts, but the caller's token is cancelled at the moment of acceptance, so the journal SaveChanges
        // that follows the accept throws — reproducing the accepted-but-not-journaled window without an in-memory DB fault.
        using CancellationTokenSource cancelAtAccept = new();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                cancelAtAccept.Cancel();
                return new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch);
            });

        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, cancelAtAccept.Token);
        }
        catch (OperationCanceledException)
        {
            // The transmit→journal window surfaced as a cancellation (the order may be live and unrecorded, gh#577) —
            // tolerated here; the durable state and the absence of a re-fire below carry the property under test.
        }

        await using (TradingCopilotDbContext afterFault = Context())
        {
            (await afterFault.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(
                ConditionalStatus.Firing, "an accepted-but-unjournaled fire is left mid-firing, never back at Pending");
            (await afterFault.Orders.AnyAsync()).Should().BeFalse("the journal commit did not land");
        }

        // Second quote on the SAME cross: the mid-firing conditional must NOT re-fire — discovery is Pending-only.
        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0, "a mid-firing conditional is never re-decided from a quote");
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(ConditionalStatus.Firing);
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessQuote_ShouldSurfaceLoudly_WhenAShutdownInterruptsTheSendBeforeTheFireIsJournaled()
    {
        // gh#577 review: the most dangerous window — a host shutdown landing WHILE the send is in flight, before the
        // venue's accept is observed — must not be silent. Unlike the accepted-then-journal-failed case, the send here
        // throws before returning Placed, so the old `transmitted` flag was never set and the operator got NO signal
        // until the next full restart. The loud log is now keyed off the durable Firing intent (committed before the
        // send), so this window is surfaced: the record is left Firing AND a LogError is emitted.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        using CancellationTokenSource cancelDuringSend = new();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes(() => cancelDuringSend.Cancel())                                // the shutdown lands mid-send…
            .Throws(() => new OperationCanceledException(cancelDuringSend.Token));    // …and the send is interrupted, outcome unknown

        ILogger<ConditionalFiringService> logger = A.Fake<ILogger<ConditionalFiringService>>();
        try
        {
            await Service(logger).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, cancelDuringSend.Token);
        }
        catch (OperationCanceledException)
        {
            // The interruption propagates to stop the pass (as at host shutdown); the loud log fires before it does.
        }

        // Left Firing so it can never blind-re-fire, and nothing journaled (the send never reached the accept)…
        await using TradingCopilotDbContext afterFault = Context();
        (await afterFault.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Firing, "a send interrupted after the intent committed is left mid-firing, never back at Pending");
        (await afterFault.Orders.AnyAsync()).Should().BeFalse();

        // …and, the point of this fix: the operator got a loud signal, not silence, even though `transmitted` was never set.
        A.CallTo(logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFireAMidFiringConditional_SoADiscardedJournalNeverReplaysAsADuplicate()
    {
        // gh#577 — the guard behind the fix. A conditional durably marked Firing (a fire whose journal did not commit)
        // is inert to the firing pass: like a resolved one it is never picked up by a crossing quote (discovery is
        // Pending-only), so a live-but-unjournaled order is reconciled / surfaced, never re-fired.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Firing);

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5320m, ask: 5321m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Firing);
    }
}
