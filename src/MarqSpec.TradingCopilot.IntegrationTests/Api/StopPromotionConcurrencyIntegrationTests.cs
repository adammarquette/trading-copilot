using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>position-aware stop promotion + self-cancel on a lost race</b> (gh#274 ⇒ gh#263,
/// R-11, R-12, ADR-0007) against <b>real PostgreSQL</b>. <see cref="StopPromotionService.PromoteForQuoteAsync"/>
/// promotes a hidden stop to a native venue order only while the venue still reports the position open, and — when a
/// concurrent retire commits between its place and its staging re-read — <b>cancels the leg it just placed</b>
/// rather than clobbering <c>Retired</c> back to <c>Native</c>.
/// </summary>
/// <remarks>
/// The unit suite can only <i>simulate</i> the race: the EF InMemory provider cannot reproduce a genuine
/// concurrent write / lost update. Here the retire is committed on a <b>separate DI scope</b> (a second connection)
/// inside the adversarial stub's <c>PlaceOrderAsync</c> — the exact window between the promotion's transmit and its
/// <c>ReloadAsync</c> — so the reload reads a genuinely-committed retire from another connection, exactly as the
/// running watcher would meet an <c>OcoExitService</c>/cancel retire. The venue seam only ever echoes seeded state
/// (positions, the ids it handed back); it never computes the promote/skip decision under test. Hosts are stripped
/// by <see cref="OcoExitTestPostgresFactory"/>, and the service is driven directly (background plumbing has no
/// authenticated user, so its reads <c>IgnoreQueryFilters</c>). Traces R-11 · R-12 · ADR-0007; verifies gh#263.
/// </remarks>
public class StopPromotionConcurrencyIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string ContractKey = "CON.F.US.MES.U26";
    private const string AccountKey = "9001";

    private readonly OcoExitTestPostgresFactory _factory;

    public StopPromotionConcurrencyIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(0)]   // flat — the position is gone
    [InlineData(-2)]  // reversed — a long's contract now shows a short
    public async Task Promote_ShouldSkip_WhenVenuePositionIsNotOpenOnTheEnteredSide(int netQuantity)
    {
        // A due hidden stop, but the venue does NOT report an open position on the entered side. Promoting would
        // rest a native protective stop for a position that is not there (the gh#183 hazard). Fail closed: skip,
        // transmit nothing, leave the plan Hidden for OcoExitService to retire on the exit event.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        Guid orderId = await SeedHiddenStopAsync(operatorId, accountId);
        VenueFactory.SeedPosition(AccountKey, ContractKey, netQuantity);

        (int promoted, _) = await PromoteAsync(bid: 5_296m, ask: 5_296.25m);

        promoted.Should().Be(0, "a stop is never promoted against a position the venue does not report open on the entered side");
        VenueFactory.TotalPlacedOrderCount.Should().Be(0, "no native stop is transmitted when the position is not open");
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden, "the plan is left hidden — nothing changed");
    }

    [Fact]
    public async Task Promote_ShouldSelfCancelTheJustPlacedLeg_WhenThePlanIsRetiredConcurrently()
    {
        // The position reads open, so promotion transmits. WHILE the place is in flight a concurrent retire commits
        // (Hidden -> Retired) on a second connection. On re-reading the staging the promotion finds it no longer
        // Hidden and must (a) NOT clobber Retired back to Native, and (b) cancel the native stop it just placed.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        Guid orderId = await SeedHiddenStopAsync(operatorId, accountId);
        VenueFactory.SeedPosition(AccountKey, ContractKey, 2); // open long, matches order size
        RetireConcurrentlyOnFirstPlace(orderId);

        (int promoted, _) = await PromoteAsync(bid: 5_296m, ask: 5_296.25m);

        promoted.Should().Be(0, "a plan retired mid-place is not promoted");
        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired, "the concurrent retire is never clobbered back to Native");
        VenueFactory.CancelOrderCalls.Should().ContainSingle("the promoter cancels the dangling leg it just placed — and nothing else");
        VenueFactory.PlacedVenueOrderIds.Should().Contain(
            VenueFactory.CancelOrderCalls.Single().VenueOrderKey,
            "the self-cancel targets the very leg this promotion placed, not some other order");
    }

    [Fact]
    public async Task Promote_ShouldLogSyntheticRisk_AndNotThrow_WhenTheDanglingLegCannotBeCancelled()
    {
        // Same lost race, but the self-cancel itself is rejected by the venue. A live resting native stop now has no
        // position behind it — the synthetic_risk case. The pass must NOT throw (the other stops must still process),
        // must surface it high-severity for manual intervention, and must still leave the winner's Retired intact.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        Guid orderId = await SeedHiddenStopAsync(operatorId, accountId);
        VenueFactory.SeedPosition(AccountKey, ContractKey, 2);
        RetireConcurrentlyOnFirstPlace(orderId);
        VenueFactory.MakeCancelsThrow(); // the dangling-leg cancel is rejected

        (int promoted, CapturingLogger log) = await PromoteAsync(bid: 5_296m, ask: 5_296.25m);

        promoted.Should().Be(0);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired, "the retire stands even though the leg could not be pulled");
        log.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("synthetic_risk", StringComparison.Ordinal),
            "a live resting order with no position behind it is surfaced at error for manual intervention");
    }

    [Fact]
    public async Task Promote_ShouldPromoteTheOtherStop_WhenOneLosesTheRaceInTheSamePass()
    {
        // Two due hidden stops in one pass, the position open. On the FIRST placement a concurrent retire hits plan A
        // only. Whichever order that first place was for, A ends self-cancelled and B still promotes — proving one
        // plan's lost race is isolated (per-record reload + save), not a batched save that would roll B back too.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        Guid orderA = await SeedHiddenStopAsync(operatorId, accountId);
        Guid orderB = await SeedHiddenStopAsync(operatorId, accountId);
        VenueFactory.SeedPosition(AccountKey, ContractKey, 2);
        RetireConcurrentlyOnFirstPlace(orderA);

        (int promoted, _) = await PromoteAsync(bid: 5_296m, ask: 5_296.25m);

        promoted.Should().Be(1, "the stop that did not lose the race still promotes — the lost race is isolated per record");
        VenueFactory.CancelOrderCalls.Should().ContainSingle("only the losing plan's dangling leg is cancelled");
        (await StagingAsync(orderA)).Should().Be(StopStaging.Retired, "the plan that lost the race stays Retired");
        (await StagingAsync(orderB)).Should().Be(StopStaging.Native, "the other plan is promoted to native");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Runs the promotion for one quote on a dedicated scope, with a capturing logger so a test can assert
    /// on the synthetic_risk log. The service is constructed directly — it is background plumbing (no user).</summary>
    private async Task<(int Promoted, CapturingLogger Log)> PromoteAsync(decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        CapturingLogger log = new();
        StopPromotionService service = new(db, log);
        ITradingVenue venue = VenueFactory.Create(FirmConventions.None);
        int promoted = await service.PromoteForQuoteAsync(
            VenueId.Parse("projectx"), ContractKey, bid, ask, venue, CancellationToken.None);
        return (promoted, log);
    }

    /// <summary>Arms a genuine concurrent retire: on the FIRST <c>PlaceOrderAsync</c> of the pass (whichever order it
    /// is for), commit <paramref name="orderId"/>'s plan to <c>Retired</c> on a SEPARATE scope/connection — the real
    /// lost-update at the promotion's race window.</summary>
    private void RetireConcurrentlyOnFirstPlace(Guid orderId)
    {
        bool fired = false;
        VenueFactory.OnPlaceOrder(async () =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            await using AsyncServiceScope concurrent = _factory.Services.CreateAsyncScope();
            TradingCopilotDbContext db = concurrent.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            StopPlanRecord plan = await db.StopPlans.IgnoreQueryFilters().SingleAsync(candidate => candidate.OrderId == orderId);
            plan.Staging = StopStaging.Retired;
            await db.SaveChangesAsync();
        });
    }

    private async Task<Guid> SeedAccountAsync(Guid operatorId)
    {
        VenueFactory.ResetPositions();
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Connections.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Firms.IgnoreQueryFilters().ExecuteDeleteAsync();

            db.Firms.Add(new Firm { Id = firmId, UserId = operatorId, Name = "Topstep", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = operatorId,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = "topstep-main",
            });
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = operatorId,
                ConnectionId = connectionId,
                VenueAccountKey = AccountKey,
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
            });
            await db.SaveChangesAsync();
        });
        return accountId;
    }

    /// <summary>Seeds a working order (long, entry 5300 / stop 5295 / safety 5290, size 2) and its <b>hidden</b> stop
    /// plan (4-tick band → promotes on a bid ≤ 5296). Returns the order id.</summary>
    private async Task<Guid> SeedHiddenStopAsync(Guid operatorId, Guid accountId)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = ContractKey,
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 2,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                EntryPrice = 5_300m,
                WorkingStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ReferencePrice = 5_300m,
                TickSize = 0.25m,
                PointValue = 5m,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_300m,
                ActualStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = StopStaging.Hidden,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private Task<StopStaging> StagingAsync(Guid orderId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(plan => plan.OrderId == orderId).Select(plan => plan.Staging).SingleAsync());

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    /// <summary>A minimal <see cref="ILogger{T}"/> that records what the service logged, so the synthetic_risk
    /// emergency log can be asserted on directly (the service is constructed with it).</summary>
    private sealed class CapturingLogger : ILogger<StopPromotionService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
