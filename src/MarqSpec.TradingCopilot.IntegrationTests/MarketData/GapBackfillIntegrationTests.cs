using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Integration coverage for <b>event-log gap → backfill recovery</b> (gh#514 ⇒ gh#306, R-1) against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>GapBackfillService</c> carries a green unit suite and has <b>never met Npgsql</b>. Two of its reads are
/// relational: a projection-then-<c>Distinct()</c> into a <i>private nested record</i> over
/// <c>Orders</c>, and the <c>Bars</c> hypertable range read. Neither has ever been translated.
/// </para>
/// <para>
/// <b>Why that is the whole point.</b> A translation failure throws, and
/// <c>StopPromotionHost.BackfillAsync</c>'s catch-all converts it into "resuming at the head without recovery" —
/// so after a restart or an event-log gap, a hidden stop whose band the blind window crossed simply never
/// promotes. Silently, permanently, with every unit test green. These cases drive the service <b>directly</b>
/// rather than through the host, precisely so that failure surfaces as a red test instead of a log line.
/// </para>
/// </remarks>
public class GapBackfillIntegrationTests : IClassFixture<GapBackfillTestPostgresFactory>
{
    private readonly GapBackfillTestPostgresFactory _factory;

    public GapBackfillIntegrationTests(GapBackfillTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string ContractKey = "CON.F.US.MES.U26";
    private const string Symbol = "MES";
    private const string OtherKey = "CON.F.US.NQ.U26";
    private const string AccountKey = "9001";
    private const string VenueKey = "projectx";

    // Offset-zero deliberately: Npgsql maps these to `timestamp with time zone`, and a non-zero offset round-trips
    // to a different instant, silently shifting the half-open [from, to) boundary the range read depends on.
    private static DateTimeOffset From { get; } = new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset To { get; } = From.AddMinutes(60);

    [Fact]
    public async Task Replay_ShouldResolveOneInstrumentPerContract_ThroughTheRelationalProjection()
    {
        // THE gh#514 PREMISE, and the case every other one here depends on. Four things must translate together at
        // the Orders read: List<Guid>.Contains → = ANY(@p); a constructor projection into a PRIVATE nested record;
        // Distinct() applied over that projection; and `Symbol ?? Instrument` → COALESCE. The unit suite exercises
        // none of them — its provider evaluates the whole expression client-side.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);

        // Two hidden stops on ONE contract, so Distinct() has something to collapse...
        await SeedHiddenStopAsync(operatorId, accountId, ContractKey, Symbol);
        await SeedHiddenStopAsync(operatorId, accountId, ContractKey, Symbol);
        // ...and a third on another contract with a NULL Symbol, so the COALESCE arm is actually taken.
        await SeedHiddenStopAsync(operatorId, accountId, OtherKey, symbol: null);

        // Lows deliberately clear of any band: this case makes no claim about promotion, only about the read.
        await SeedMinuteBarsAsync(Symbol, count: 60, high: 5_310m, low: 5_297m);
        await SeedMinuteBarsAsync(OtherKey, count: 60, high: 5_310m, low: 5_297m);

        GapBackfillOutcome outcome = await ReplayAsync();

        outcome.Coverage.Should().HaveCount(2,
            "two hidden stops on the same contract collapse to ONE instrument through the Distinct(); the third, "
            + "on another contract, is the second entry. Three would mean the Distinct() did not translate");
        outcome.Coverage.Select(instrument => instrument.ContractKey).Should().BeEquivalentTo([ContractKey, OtherKey]);
        outcome.Coverage.Single(instrument => instrument.ContractKey == ContractKey).Symbol.Should().Be(Symbol);
        outcome.Coverage.Single(instrument => instrument.ContractKey == OtherKey).Symbol.Should().Be(OtherKey,
            "an order with no Symbol falls back to its contract key through the COALESCE arm");
        outcome.IsComplete.Should().BeTrue("sixty one-minute bars cover the whole sixty-minute window");
    }

    [Fact]
    public async Task Replay_ShouldPromoteAHiddenStop_WhoseBandTheWindowsAdverseExtremeCrossed()
    {
        Guid orderId = await OneHiddenStopWithAPositionAsync();

        // The window's LOW becomes the synthetic quote's BID — a long is protected by a stop below and is measured
        // on the bid, so the low is its worst case. Band = ProximityValue × the ORDER's TickSize = 4 × 0.25 = 1.00,
        // so a Buy promotes at or below 5295 + 1.00. This low sits EXACTLY on that inclusive boundary.
        await SeedMinuteBarsAsync(Symbol, count: 60, high: 5_310m, low: 5_296m);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        GapBackfillOutcome outcome = await ReplayAsync();

        outcome.Promoted.Should().Be(1);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Native,
            "the promotion must be COMMITTED to Postgres, not merely counted in the returned outcome");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1,
            "a promoted stop is a real order at the venue — recovery that only updates the row would leave the "
            + "position unprotected while claiming otherwise");
    }

    [Fact]
    public async Task Replay_ShouldNotPromote_WhenTheWindowsWorstExcursionNeverReachedTheBand()
    {
        Guid orderId = await OneHiddenStopWithAPositionAsync();

        // Identical to the case above in every respect but ONE SCALAR: this low is a single tick further away,
        // 5297 against 5296. 5296 <= 5296.00 holds; 5297 <= 5296.00 does not. That one tick is the entire
        // hypothesis, and it is what pins the boundary as inclusive rather than exclusive.
        await SeedMinuteBarsAsync(Symbol, count: 60, high: 5_310m, low: 5_297m);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        GapBackfillOutcome outcome = await ReplayAsync();

        outcome.Promoted.Should().Be(0);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden);
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore,
            "nothing may be transmitted when the band was never crossed");

        // Holds coverage constant as an ASSERTED fact rather than an assumed one. Without it this case would also
        // pass when the bars were mis-keyed and no bars were found at all — an abstention for the wrong reason,
        // which would make the pair prove nothing about price.
        outcome.IsComplete.Should().BeTrue(
            "the abstention must be about PRICE, not about missing bars");
    }

    [Fact]
    public async Task Replay_ShouldMeasureCoverageFromTheFinestStoredResolution_AndMeterTheRealShortfall()
    {
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        await SeedHiddenStopAsync(operatorId, accountId, ContractKey, Symbol);

        // Two resolutions over one window, which is the state no test has ever produced — the existing bar seeding
        // hardcodes a single resolution, so the Min() that picks the finest series has never had a second value to
        // choose between.
        //
        // The hourly bar spans the whole window; the five-minute series covers only its first ten minutes. Coverage
        // must be measured from the FINE series and report fifty minutes uncovered. Measuring it from the hourly
        // bar would report the window fully covered — a partial recovery believed to be a complete one, which the
        // outcome's own contract calls worse than the silence it replaced.
        await SeedBarAsync(Symbol, resolutionMinutes: 60, bucketStart: From, high: 5_310m, low: 5_290m);
        await SeedBarAsync(Symbol, resolutionMinutes: 5, bucketStart: From, high: 5_310m, low: 5_297m);
        await SeedBarAsync(Symbol, resolutionMinutes: 5, bucketStart: From.AddMinutes(5), high: 5_310m, low: 5_297m);

        _factory.Capture.Clear();

        GapBackfillOutcome outcome = await ReplayAsync();

        outcome.IsComplete.Should().BeFalse();
        outcome.Shortfalls.Should().ContainSingle();
        outcome.Shortfalls[0].Coverage.Shortfall.Should().Be(TimeSpan.FromMinutes(50),
            "the five-minute series covers From→From+10 and the window runs to From+60; the hourly bar spanning "
            + "the whole window must not be credited, or a fifty-minute blind spot is reported as full coverage");

        // The metric is recorded inside the service rather than at the caller, so every consumer of a backfill
        // gets the shortfall — not only whichever host remembered to log it.
        SliCapture.Measurement shortfall = _factory.Capture
            .For(ExecutionMetrics.BackfillShortfall)
            .Should().ContainSingle().Subject;
        shortfall.Value.Should().Be(TimeSpan.FromMinutes(50).TotalMilliseconds,
            "the metered duration must be the real uncovered time, not a flag that something was missed");
        shortfall.Tags.Should().ContainKey("contract");
    }

    [Fact]
    public async Task Replay_ShouldMeterNothing_WhenTheWindowWasFullyCovered()
    {
        // The other half of the shortfall contract: a healthy recovery is SILENT. A histogram that emitted on every
        // pass would make the alert fire on ordinary sessions, and an alert that fires on healthy days gets muted.
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        await SeedHiddenStopAsync(operatorId, accountId, ContractKey, Symbol);
        await SeedMinuteBarsAsync(Symbol, count: 60, high: 5_310m, low: 5_297m);

        _factory.Capture.Clear();

        GapBackfillOutcome outcome = await ReplayAsync();

        outcome.IsComplete.Should().BeTrue();
        _factory.Capture.All
            .Where(measurement => measurement.Instrument == ExecutionMetrics.BackfillShortfall)
            .Should().BeEmpty("a fully covered window has no shortfall to report");
    }

    [Fact]
    public async Task Replay_ShouldPromoteExactlyOnce_WhenTheSameGapIsReplayedTwice()
    {
        Guid orderId = await OneHiddenStopWithAPositionAsync();
        await SeedMinuteBarsAsync(Symbol, count: 60, high: 5_310m, low: 5_296m);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        GapBackfillOutcome first = await ReplayAsync();
        GapBackfillOutcome second = await ReplayAsync();

        first.Promoted.Should().Be(1);
        second.Promoted.Should().Be(0,
            "the plan is no longer Hidden after the first pass, so the second finds nothing to recover");
        (await StagingAsync(orderId)).Should().Be(StopStaging.Native);
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1,
            "a replayed gap must not place a SECOND protective stop — a duplicate resting order on one position "
            + "is exactly what the host's replay-without-advancing-the-cursor behaviour could produce");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private Task SeedBarAsync(string symbol, int resolutionMinutes, DateTimeOffset bucketStart, decimal high, decimal low) =>
        ExecuteDbAsync(async db =>
        {
            db.Bars.Add(NewBar(symbol, resolutionMinutes, bucketStart, high, low));
            await db.SaveChangesAsync();
        });

    /// <summary>
    /// The shared arrangement for the promotion pair: one account, exactly one hidden stop on one instrument, and
    /// the venue reporting a matching open position.
    /// </summary>
    /// <remarks>
    /// The seeded position is a <b>hard precondition</b>, not decoration: promotion is skipped outright unless the
    /// venue reports a snapshot on the same contract with a non-zero quantity on the matching side. Omit it and the
    /// stop abstains regardless of price — which would make the negative case above pass for the wrong reason.
    /// </remarks>
    private async Task<Guid> OneHiddenStopWithAPositionAsync()
    {
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SeedAccountAsync(operatorId);
        Guid orderId = await SeedHiddenStopAsync(operatorId, accountId, ContractKey, Symbol);
        VenueFactory.SeedPosition(AccountKey, ContractKey, netQuantity: 2);
        return orderId;
    }

    private Task<StopStaging> StagingAsync(Guid orderId) => QueryDbAsync(db => db.StopPlans
        .IgnoreQueryFilters()
        .Where(plan => plan.OrderId == orderId)
        .Select(plan => plan.Staging)
        .SingleAsync());

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>
    /// Drives the service <b>directly</b>. Never through <c>StopPromotionHost</c>: its catch-all would turn the
    /// translation failure this suite exists to detect into a log line and a green test.
    /// </summary>
    private async Task<GapBackfillOutcome> ReplayAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        GapBackfillService backfill = scope.ServiceProvider.GetRequiredService<GapBackfillService>();
        IProjectXVenueFactory venues = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();
        ITradingVenue venue = venues.Create(FirmConventions.None);
        return await backfill.ReplayForStopPromotionAsync(From, To, venue.Id, venue, CancellationToken.None);
    }

    /// <summary>
    /// Wipes every table the backfill scans and stands up one account. The wipe is not hygiene: the hidden-plan
    /// scan carries <b>no</b> user, venue, instrument or account predicate, so a plan left by an earlier test in
    /// this class silently drifts every count here.
    /// </summary>
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
            await db.Bars.ExecuteDeleteAsync();

            db.Firms.Add(new Firm { Id = firmId, UserId = operatorId, Name = "Topstep", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = operatorId,
                FirmId = firmId,
                Platform = VenueKey,
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

    /// <summary>
    /// A long working order (entry 5300 / working 5295 / safety 5290, size 2) and its <b>hidden</b> plan with a
    /// four-tick band — so it promotes on a bid at or below 5296. Returns the order id.
    /// </summary>
    private async Task<Guid> SeedHiddenStopAsync(Guid operatorId, Guid accountId, string contractKey, string? symbol)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = contractKey,
                Symbol = symbol,
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

    private Task SeedMinuteBarsAsync(string symbol, int count, decimal high, decimal low) =>
        ExecuteDbAsync(async db =>
        {
            for (int minute = 0; minute < count; minute++)
            {
                db.Bars.Add(NewBar(symbol, resolutionMinutes: 1, From.AddMinutes(minute), high, low));
            }

            await db.SaveChangesAsync();
        });

    private static BarRecord NewBar(string symbol, int resolutionMinutes, DateTimeOffset bucketStart, decimal high, decimal low) =>
        new()
        {
            Venue = VenueKey,
            Instrument = symbol, // the SYMBOL the bar store is keyed on, never the contract key
            ResolutionMinutes = resolutionMinutes,
            BucketStart = bucketStart,
            Open = low,
            High = high,
            Low = low,
            Close = high,
            Volume = 10,
            RecordedAt = From,
        };

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
