using System.Text.Json;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Gap recovery driven through the <b>live hosts</b> (gh#557 ⇒ gh#306, R-1): the three cases gh#514 could not
/// deliver, because they need the <i>opposite</i> fixture to the one that suite depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second fixture.</b> <see cref="GapBackfillIntegrationTests"/> strips every hosted service, and that is
/// load-bearing there: with <c>StopPromotionHost</c> alive a promotion count is true whether or not the backfill
/// did anything, and <c>VenueConnectionMonitorHost</c> flips every <c>Hidden</c> plan to <c>Orphaned</c> on its
/// first pass — the backfill reads only <c>Hidden</c>, so its subject would quietly cease to exist. These three
/// cases are about what the <b>host</b> does around the service — which window it hands it, whether it runs it at
/// all, and whether the consumer survives it throwing — so here the hosts must be <b>running</b>.
/// </para>
/// <para>
/// <b>Two traps this suite is built around.</b> (1) The existing retention-gap recipe appends its quote on
/// <c>MES</c>; the live batch loop feeds that straight to <c>PromoteForQuoteAsync</c>, so a seeded <c>MES</c> plan
/// would promote through the <i>ordinary</i> path and "recovery promoted nothing" would pass for entirely the wrong
/// reason. Every gap here is therefore manufactured with a quote on <see cref="GapContract"/>, which the promotion
/// path filters out by instrument. (2) <c>VenueConnectionMonitorHost</c> latches <c>wasConnected</c> after its
/// first pass and does not re-enter the branch while the state is unchanged — so the hidden stop is seeded
/// <b>after</b> the cursor catch-up, and <c>Staging == Hidden</c> is asserted as an explicit precondition
/// immediately before the act.
/// </para>
/// <para>
/// <b>The window is real, not a constant.</b> A host's recovery window is
/// <c>[cursor.CommittedAt, gap.OldestAvailableOccurredAt]</c> — both wall-clock instants of the run. Rather than
/// invent one, these cases <b>back-date the committed cursor</b> (the faithful shape: a consumer that last
/// committed ten minutes ago and then fell off the back of the log) and seed bars inside the window that opens up.
/// </para>
/// <para>
/// The venue seam is the sole sanctioned double and stays adversarial: it feeds positions and bars and records what
/// was transmitted, and never returns a production-computed answer. Traces R-1 · gh#306 · ADR-0001 · ADR-0013.
/// </para>
/// </remarks>
public class GapBackfillHostRecoveryIntegrationTests : IClassFixture<RetentionConsumerTestPostgresFactory>
{
    /// <summary>The contract the seeded stops and conditionals live on — never the one a manufactured gap quotes.</summary>
    private const string MesContract = "CON.F.US.MES.U26";

    /// <summary>The symbol the bar store is keyed on for <see cref="MesContract"/>.</summary>
    private const string MesSymbol = "MES";

    /// <summary>
    /// The contract every manufactured gap is quoted on. <b>Deliberately not <see cref="MesContract"/></b> — see the
    /// class remarks' trap (1): a live quote on the seeded contract promotes through the ordinary path.
    /// </summary>
    private const string GapContract = "CON.F.US.NQ.U26";

    private const string AccountKey = "9001";
    private const string VenueKey = "projectx";

    private readonly RetentionConsumerTestPostgresFactory _factory;

    public GapBackfillHostRecoveryIntegrationTests(RetentionConsumerTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // 1. A conditional the blind window crossed is REPORTED by the live host — and never fired.
    // =============================================================================================================

    [Fact]
    public async Task Backfill_ShouldLeaveAPendingConditionalPending_WhenTheBlindWindowCrossedItsTrigger()
    {
        // The positive detection carries the weight. DetectMissedTriggersAsync takes no ITradingVenue and performs
        // no save, so it is STRUCTURALLY incapable of firing — "still Pending, zero placements" is equally true when
        // the read returns nothing, when the bars are mis-keyed, and when it throws (the host swallows that at
        // Error). Those assertions are kept because they pin the deliberate report-never-fire decision, but the
        // guard is that exactly the CROSSED conditionals are named, with their trigger prices and directions, in
        // the host's own log — the only place this consumer surfaces the result.
        Guid accountId = await ResetWorldAsync();

        long consumed = await AppendQuoteAndAwaitCatchUpAsync(ConditionalOrderHost.ConsumerGroup);

        // The recovery window: a consumer that last committed ten minutes ago, then fell off the back of the log.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        await BackdateCursorAsync(ConditionalOrderHost.ConsumerGroup, windowStart);

        // The window's extremes will be high 5310 / low 5296.
        Guid crossedUp = await SeedPendingConditionalAsync(accountId, 5_305m, ConditionalCrossDirection.RisesTo);
        Guid crossedDown = await SeedPendingConditionalAsync(accountId, 5_296m, ConditionalCrossDirection.FallsTo);
        // The control: above the window's high, so it was never crossed. Without it, a detector that reported EVERY
        // pending conditional would satisfy the two positive assertions perfectly.
        Guid uncrossed = await SeedPendingConditionalAsync(accountId, 5_320m, ConditionalCrossDirection.RisesTo);

        await SeedMinuteBarsAsync(MesSymbol, windowStart, count: 9, high: 5_310m, low: 5_296m);

        int placedBefore = VenueFactory.TotalPlacedOrderCount;
        long survivor = await ManufactureGapAsync(consumed);

        // The host reports one line per missed trigger; wait for both crossed ones to land.
        await WaitUntilAsync(
            () => Task.FromResult(MissedTriggerLines().Count >= 2),
            "the conditional-order host to report both crossed conditionals as missed triggers");

        // Then let the pass FINISH before reading state. Recovery runs before the batch is consumed, so waiting for
        // the cursor to clear the gap is what makes the "never fired" assertions below a statement about the system
        // rather than a race the assertions happen to win.
        await WaitUntilAsync(
            async () => await CursorAsync(ConditionalOrderHost.ConsumerGroup) >= survivor,
            $"the conditional-order consumer to finish the pass and advance past the gap to sequence {survivor}");

        IReadOnlyList<string> reported = MissedTriggerLines();

        reported.Should().HaveCount(2,
            "exactly the two conditionals the window crossed are reported — a third line would mean the uncrossed "
            + "control was reported too, and a detector that names everything names nothing");
        reported.Should().Contain(line => line.Contains(crossedUp.ToString(), StringComparison.Ordinal),
            "the RisesTo 5305 is judged against the window's HIGH of 5310 — it was crossed");
        reported.Should().Contain(line => line.Contains(crossedDown.ToString(), StringComparison.Ordinal),
            "the FallsTo 5296 is judged against the window's LOW of 5296 — it was crossed");
        reported.Should().NotContain(line => line.Contains(uncrossed.ToString(), StringComparison.Ordinal),
            "the RisesTo 5320 sits above the window's high of 5310 and must NOT be reported");

        // The report is actionable only if it carries WHAT would have fired and WHERE price actually got to.
        string upLine = reported.Single(line => line.Contains(crossedUp.ToString(), StringComparison.Ordinal));
        upLine.Should().Contain("RisesTo", "the operator needs the direction it would have crossed")
            .And.Contain("5305", "…the price it would have fired at…")
            .And.Contain(MesContract, "…and the CONTRACT to act on, not the bar store's symbol");

        // The deliberate non-recovery, pinned: reported is not fired, and nothing was transmitted.
        int stillPending = await QueryDbAsync(db => db.ConditionalOrders
            .IgnoreQueryFilters()
            .CountAsync(order => order.Status == ConditionalStatus.Pending));
        stillPending.Should().Be(3, "a crossed conditional is REPORTED, never fired — and never advanced");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore,
            "recovery reports a missed conditional; firing an entry on a cross this old would act on stale grounds");
    }

    // =============================================================================================================
    // 2. No committed cursor ⇒ no lower bound ⇒ recovery is SKIPPED rather than run against an invented window.
    // =============================================================================================================

    [Fact]
    public async Task LiveHost_ShouldSkipRecoveryAndPromoteNothing_WhenNoCommittedCursorGivesTheWindowALowerBound()
    {
        // A gap needs a lower bound to recover FROM, and the only durable one is the cursor's commit time.
        // Inventing one would fabricate the size of the blind window (engineering §9), so the host must say so and
        // resume at the head. The hazard this guards is the opposite: a fabricated bound would replay a window
        // nobody observed and promote against it.
        //
        // Note the shape the event log forces here: ReadAfterAsync NEVER reports a gap for cursor 0 ("a fresh group
        // reads from the start of whatever survives — by definition not a gap"), so the host must hold an
        // IN-MEMORY cursor past zero while the durable commit row is absent. Catching up and then removing the row
        // is the only way to reach the branch — and it is a real shape: the process keeps its memory while the
        // cursor row is lost underneath it.
        Guid accountId = await ResetWorldAsync();

        long consumed = await AppendQuoteAndAwaitCatchUpAsync(StopPromotionHost.ConsumerGroup);

        // Trap (2): seeded AFTER catch-up, so VenueConnectionMonitorHost's first pass has already latched and
        // cannot orphan this plan out from under the assertion.
        Guid orderId = await SeedHiddenStopWithPositionAsync(accountId);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden,
            "PRECONDITION: the plan must still be Hidden when the act begins — an Orphaned plan is invisible to the "
            + "backfill, and the promoted-nothing assertion below would then hold for the wrong reason");

        // Bars that WOULD promote, spanning the hour before the gap: if the host ever substituted a fabricated
        // lower bound, recovery would run over them and promote — which is exactly what this case must be able to
        // catch, and what makes "promoted nothing" a statement about the SKIP rather than about missing data.
        await SeedMinuteBarsAsync(MesSymbol, DateTimeOffset.UtcNow.AddMinutes(-59), count: 55, high: 5_310m, low: 5_296m);

        await DeleteCursorRowAsync(StopPromotionHost.ConsumerGroup);

        int placedBefore = VenueFactory.TotalPlacedOrderCount;
        long survivor = await ManufactureGapAsync(consumed);

        await WaitUntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains($"Gap on {StopPromotionHost.ConsumerGroup} cannot be backfilled", StringComparison.Ordinal)
                && entry.Message.Contains("no committed cursor", StringComparison.Ordinal))),
            "the stop-promotion host to report that the gap cannot be backfilled without a committed cursor");

        // Skipping recovery is not the same as stalling: the consumer resumes at the head. This wait is ALSO what
        // makes the two assertions below sound — the backfill runs before the batch is consumed, so a host that
        // fabricated a lower bound and promoted would have done so by the time the cursor clears the gap. Asserting
        // straight off the log instead lets a broken host pass simply by not having finished yet.
        await WaitUntilAsync(
            async () => await CursorAsync(StopPromotionHost.ConsumerGroup) >= survivor,
            $"the stop-promotion consumer to resume past the gap to sequence {survivor}");

        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden,
            "recovery was skipped, so the plan is untouched — the native safety stop remains the floor");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore,
            "a skipped recovery transmits nothing; a promotion here would mean the window was invented");
    }

    // =============================================================================================================
    // 3. A recovery that throws must not take the consumer down with it.
    // =============================================================================================================

    [Fact]
    public async Task LiveConsumer_ShouldKeepConsuming_WhenTheRecoveryPathThrows()
    {
        // Recovery is strictly additive to a gap signal that has already been reported at Error. A failure inside it
        // must degrade to the pre-gh#306 behaviour — resume at the head without recovery — never escalate a recovery
        // problem into an outage on the safety-critical live path.
        //
        // The fault is injected at PlaceOrderAsync, NOT at GetPositionsAsync: the latter fails CLOSED upstream
        // (OpenQuantityAsync swallows it and abstains), so it would prove the wrong thing — a quiet non-promotion
        // rather than a thrown recovery.
        Guid accountId = await ResetWorldAsync();

        long consumed = await AppendQuoteAndAwaitCatchUpAsync(StopPromotionHost.ConsumerGroup);

        DateTimeOffset windowStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        await BackdateCursorAsync(StopPromotionHost.ConsumerGroup, windowStart);

        Guid orderId = await SeedHiddenStopWithPositionAsync(accountId);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden,
            "PRECONDITION: the plan must be Hidden, or the backfill has nothing to act on and could not throw");

        // Low 5296 sits exactly on the inclusive promotion boundary (stop 5295 + a 4-tick band of 1.00), so
        // recovery WILL reach the venue — which is what gives the injected fault something to interrupt.
        await SeedMinuteBarsAsync(MesSymbol, windowStart, count: 9, high: 5_310m, low: 5_296m);

        VenueFactory.OnPlaceOrder(() =>
            Task.FromException(new InvalidOperationException("Venue rejected the promoted stop (test).")));

        int placedBefore = VenueFactory.TotalPlacedOrderCount;
        long survivor = await ManufactureGapAsync(consumed);

        await WaitUntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains($"Gap backfill on {StopPromotionHost.ConsumerGroup} failed", StringComparison.Ordinal))),
            "the stop-promotion host to report the failed backfill at Error");

        // THE case: the consumer is still alive and still consuming. A watcher that died, or crash-looped on the
        // same event, never reaches the survivor.
        await WaitUntilAsync(
            async () => await CursorAsync(StopPromotionHost.ConsumerGroup) >= survivor,
            $"the stop-promotion consumer to keep consuming past the failed recovery to sequence {survivor}");

        // Distinguishes "recovery ran and threw" from "recovery never ran": the venue records the request before the
        // injected fault fires, so the attempt is visible even though it failed.
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1,
            "recovery must actually have reached the venue — otherwise this case proves nothing about a THROWN "
            + "recovery, only about one that quietly did nothing");
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden,
            "the transmit threw, so the plan must NOT be recorded Native — claiming an exchange-held stop that does "
            + "not exist would be a lie on a safety path");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Every "would have fired while the log was blind" line the conditional-order host has emitted.</summary>
    private IReadOnlyList<string> MissedTriggerLines() =>
        [.. _factory.Logs.Entries
            .Where(entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("would have fired while the log was blind", StringComparison.Ordinal))
            .Select(entry => entry.Message)];

    /// <summary>
    /// Appends a quote on <see cref="GapContract"/> and waits for <paramref name="consumerGroup"/> to consume it, so
    /// the consumer holds an in-memory cursor past zero (the precondition for a gap to be reportable at all).
    /// </summary>
    private async Task<long> AppendQuoteAndAwaitCatchUpAsync(string consumerGroup)
    {
        long consumed = (await AppendGapContractQuoteAsync()).Sequence;
        await WaitUntilAsync(
            async () => await CursorAsync(consumerGroup) >= consumed,
            $"the {consumerGroup} consumer to catch up to sequence {consumed}");
        return consumed;
    }

    /// <summary>
    /// Manufactures a genuine retention hole under a caught-up consumer — delete the consumed window, burn a
    /// sequence value so the next survivor is non-contiguous, then append it. Returns the survivor's sequence.
    /// </summary>
    private async Task<long> ManufactureGapAsync(long consumedThrough)
    {
        await ExecuteDbAsync(db => db.Events.Where(evt => evt.Sequence <= consumedThrough).ExecuteDeleteAsync());
        await BurnSequenceAsync();
        return (await AppendGapContractQuoteAsync()).Sequence;
    }

    private async Task<EventEnvelope> AppendGapContractQuoteAsync()
    {
        string payload = JsonSerializer.Serialize(
            new { contract = $"{VenueKey}:{GapContract}", bid = 4_000m, ask = 4_000.25m });
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.AppendAsync(
            new EventDraft(QuoteIngestionService.QuoteEventType, VenueKey, DateTimeOffset.UtcNow, payload),
            CancellationToken.None);
    }

    // Burns one identity value so the next append is non-contiguous with the surviving window (the rolled-back /
    // dropped-chunk hole shape). ExecuteSqlRaw runs the nextval side effect even though it returns no rows.
    private Task BurnSequenceAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync("SELECT nextval(pg_get_serial_sequence('\"Events\"', 'Sequence'))"));

    private Task<long?> CursorAsync(string consumerGroup) => QueryDbAsync(async db =>
    {
        EventCursor? cursor = await db.EventCursors
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ConsumerGroup == consumerGroup);
        return cursor?.LastSequence;
    });

    /// <summary>
    /// Back-dates a consumer group's commit time, so the recovery window the host derives is wide and deterministic
    /// rather than the few milliseconds between catch-up and the manufactured gap.
    /// </summary>
    private Task BackdateCursorAsync(string consumerGroup, DateTimeOffset committedAt) => ExecuteDbAsync(async db =>
    {
        EventCursor? cursor = await db.EventCursors
            .FirstOrDefaultAsync(candidate => candidate.ConsumerGroup == consumerGroup);
        cursor.Should().NotBeNull($"the {consumerGroup} consumer must have committed a cursor before it is back-dated");
        cursor!.CommittedAt = committedAt;
        await db.SaveChangesAsync();
    });

    /// <summary>Removes the durable commit row while the running host keeps its in-memory cursor.</summary>
    private Task DeleteCursorRowAsync(string consumerGroup) => ExecuteDbAsync(db => db.EventCursors
        .Where(candidate => candidate.ConsumerGroup == consumerGroup)
        .ExecuteDeleteAsync());

    /// <summary>
    /// Wipes everything these cases seed and stands up one account, returning its id. The wipe is not hygiene: the
    /// hidden-plan scan carries no user, venue, instrument or account predicate, so a plan or conditional left by an
    /// earlier case in this class would silently drift every count here.
    /// </summary>
    private async Task<Guid> ResetWorldAsync()
    {
        _factory.Logs.Clear();
        VenueFactory.ResetPositions(); // also clears any OnPlaceOrder effect a previous case installed

        Guid operatorId = await QueryDbAsync(async db =>
            (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.ConditionalOrders.IgnoreQueryFilters().ExecuteDeleteAsync();
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
    /// A long working order (entry 5300 / working 5295 / safety 5290) with a <b>hidden</b> four-tick plan — so it
    /// promotes on a bid at or below 5296 — plus the venue position promotion refuses to act without.
    /// </summary>
    private async Task<Guid> SeedHiddenStopWithPositionAsync(Guid accountId)
    {
        Guid operatorId = await QueryDbAsync(async db =>
            (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);
        Guid orderId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = MesContract,
                Symbol = MesSymbol,
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

        // Promotion refuses outright unless the venue still reports the position open on the entered side.
        VenueFactory.SeedPosition(AccountKey, MesContract, netQuantity: 2);
        return orderId;
    }

    /// <summary>
    /// A pending conditional on the account. <c>AccountId</c> carries a real foreign key against Postgres, so the
    /// unit tier's habit of handing it a fresh <see cref="Guid"/> would violate it here.
    /// </summary>
    private async Task<Guid> SeedPendingConditionalAsync(
        Guid accountId, decimal triggerPrice, ConditionalCrossDirection direction)
    {
        Guid operatorId = await QueryDbAsync(async db =>
            (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);
        Guid conditionalId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.ConditionalOrders.Add(new ConditionalOrderRecord
            {
                Id = conditionalId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = MesContract,
                Symbol = MesSymbol,
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = 5_300m,
                WorkingStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ReferencePrice = 5_300m,
                TickSize = 0.25m,
                PointValue = 5m,
                TriggerPrice = triggerPrice,
                TriggerDirection = direction,
                Status = ConditionalStatus.Pending,
                Mode = TradingMode.Practice,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await db.SaveChangesAsync();
        });

        return conditionalId;
    }

    /// <summary>
    /// One-minute bars from <paramref name="from"/>, keyed on the <b>symbol</b> the bar store uses. Offset-zero
    /// throughout: Npgsql maps these to <c>timestamp with time zone</c>, and a non-zero offset round-trips to a
    /// different instant, silently shifting the half-open <c>[from, to)</c> window the range read depends on.
    /// </summary>
    private Task SeedMinuteBarsAsync(string symbol, DateTimeOffset from, int count, decimal high, decimal low) =>
        ExecuteDbAsync(async db =>
        {
            for (int minute = 0; minute < count; minute++)
            {
                db.Bars.Add(new BarRecord
                {
                    Venue = VenueKey,
                    Instrument = symbol,
                    ResolutionMinutes = 1,
                    BucketStart = from.AddMinutes(minute),
                    Open = low,
                    High = high,
                    Low = low,
                    Close = high,
                    Volume = 10,
                    RecordedAt = from,
                });
            }

            await db.SaveChangesAsync();
        });

    private Task<StopStaging> StagingAsync(Guid orderId) => QueryDbAsync(db => db.StopPlans
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(plan => plan.OrderId == orderId)
        .Select(plan => plan.Staging)
        .SingleAsync());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, int attempts = 150, int delayMs = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException($"Timed out waiting for {because}.");
    }

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
