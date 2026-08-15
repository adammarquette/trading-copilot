using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for gh#911, the QA card for gh#748: a <see cref="PositionEvent"/>(flat)
/// delivered to the account-event stream <b>before</b> its closing <see cref="FillEvent"/> must still journal
/// exactly one <see cref="Trade"/> once the fill lands, through a defer-then-retry path — not be lost, which is
/// what happens today. Authored from gh#911's and gh#748's own text, not from any implementation of gh#748 (the QA
/// contract's independence rule) — see the remarks below for why that matters more than usual here.
/// </summary>
/// <remarks>
/// <para>
/// <b>gh#748 is not merged into <c>develop</c>.</b> This suite was authored expecting a coding PR "merged under
/// #748" (gh#911's own premise); at authoring time there was no such PR at all, no <c>PendingFlatJournal</c>, no
/// <c>deferred</c>/<c>journalled</c>-on-retry outcome pair in <see cref="ExecutionMetrics"/>, and
/// <see cref="AccountEventStreamHost"/>'s <see cref="FillEvent"/> branch — <see cref="AccountEventIngestionService.ProcessAsync"/>
/// — only ever wrote the <see cref="Fill"/> row, never re-invoking <see cref="TradeJournalService"/>. <b>PR #912</b>
/// (branch <c>bug/748_a-flat-event-queued</c>) landed the fix moments later, concurrently with this suite's own
/// authoring, and is open but <b>unmerged</b> as of this revision: it adds exactly the shape gh#911 anticipates —
/// <c>PendingFlatJournal</c> (keyed on <c>(Account, Contract.Key, At)</c>), the <c>JournalDeferred = "deferred"</c>
/// outcome, and a retry loop in <see cref="AccountEventStreamHost"/>'s <see cref="FillEvent"/> branch — but it also
/// changes <see cref="AccountEventStreamHost"/>'s constructor to take a <c>PendingFlatJournal</c> parameter, so the
/// three cases below will need that one-line constructor-call update (the same fix #912 already applied to
/// <c>TradeFifoPairingIntegrationTests</c> and <c>TradeJournalWriteFaultIntegrationTests</c>) alongside removing
/// their <c>Skip</c>, once #912 merges and this branch rebases onto it. This suite proceeds without waiting for
/// that merge, per the QA contract's rule 3 ("a suite that cannot go green without a production change has found a
/// defect — report it, don't fix it"): three cases below assert the FIXED behaviour gh#911 specifies and are
/// <see cref="FactAttribute.Skip"/>ped, referencing gh#748 (already filed; no new issue needed — and #912, once it
/// exists, is the coding agent's PR to track, not this suite's). The other two are provable TODAY and stay green
/// against `develop` as shipped — one of them (the DEFECT pin) doubles as this suite's own "prove-red": it
/// demonstrates, without any local revert, that the adverse-order case is lost under the code on `develop` today,
/// the same code path #912 replaces.
/// </para>
/// <para>
/// <b>What's provable today vs. blocked.</b>
/// <list type="bullet">
/// <item><description><see cref="AccountEventStreamHost_ShouldComposeExactlyOneTrade_WhenTheFlatArrivesBeforeItsClosingFill"/>
/// (AC1) — Skipped; needs the retry-on-fill wiring.</description></item>
/// <item><description><see cref="AccountEventStreamHost_ShouldRecordDeferredThenJournalled_WhenTheFlatArrivesBeforeItsClosingFill"/>
/// (AC2) — Skipped; needs the <c>deferred</c> outcome to exist at all.</description></item>
/// <item><description><see cref="AccountEventStreamHost_ShouldWriteNoSecondRow_WhenTheComposedTradeIsReplayed"/>
/// (AC3) — Skipped; needs AC1's compose to have happened first.</description></item>
/// <item><description><see cref="AccountEventStreamHost_ShouldRefuseAndNeverDefer_WhenASameInstantOppositeSideTieFallsInsideTheWindow"/>
/// (AC4) — provable now: a genuine ambiguity is refused identically whether or not a defer mechanism exists, and
/// asserting it stays terminal (never <c>deferred</c>) is a real regression guard both before and after gh#748.</description></item>
/// <item><description><see cref="AccountEventStreamHost_ShouldPermanentlyLoseTheRoundTrip_WhenTheFlatArrivesBeforeItsClosingFill_PreGh748"/>
/// (AC5 / prove-red) — provable now: pins the exact defect gh#748 describes as a DEFECT-annotated regression guard
/// (QA contract rule 2), so it documents reality without enshrining it and flips into the failure signal for
/// whoever removes it once gh#748 ships.</description></item>
/// </list>
/// When gh#748 lands: remove the three <c>Skip</c>s (their assertions were written for the fixed behaviour and
/// should need no other edit beyond matching the shipped outcome literal, if it differs from the <c>"deferred"</c>
/// gh#911 names), and delete <c>..._PreGh748</c> — its failure IS the signal that the retry now exists.
/// </para>
/// <para>
/// <b>Out of scope</b> (gh#911's own list, not re-litigated here): the restart-crosses-the-race edge and a
/// genuinely-never-arriving closing fill stay with gh#722's reconcile sweep and gh#770's adopt-time fill backfill.
/// </para>
/// </remarks>
public sealed class FlatBeforeFillAdverseOrderIntegrationTests : IClassFixture<FlatBeforeFillAdverseOrderPostgresFactory>
{
    private const string Contract = "CON.F.US.MES.U26";
    private const string OurCredentialKey = "topstep-main";
    private const decimal ContractPointValue = 5m;

    private static readonly DateTimeOffset _origin = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

    private static readonly VenueId _projectx = VenueId.Parse("projectx");

    private readonly FlatBeforeFillAdverseOrderPostgresFactory _factory;

    public FlatBeforeFillAdverseOrderIntegrationTests(FlatBeforeFillAdverseOrderPostgresFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // AC1 — Adverse order composes: the flat is delivered BEFORE the closing fill; exactly one Trade composes with
    // the correct signed RealizedPnL once the fill lands, through the defer-then-retry path (not a same-pass compose).
    // =================================================================================================================

    [Fact(Skip =
        "blocked by gh#748 -- AccountEventStreamHost's FillEvent branch never retries the journal on develop today, "
        + "so an early flat's round trip cannot compose no matter how long this waits. Flips green once gh#748's "
        + "defer/retry wiring lands and the Skip is removed.")]
    public async Task AccountEventStreamHost_ShouldComposeExactlyOneTrade_WhenTheFlatArrivesBeforeItsClosingFill()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "8101");

        Guid entryFill = FillId(1, 0x01);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-8101", [(entryFill, 5_000m, 1, At(0))]);
        // The closing order is WORKING with no fill yet -- ingestion has a VenueOrderKey to resolve when the late
        // FillEvent finally arrives, exactly as the gh#799 re-pairing case seeds its late executions.
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-8101", []);

        await using RunningHost running = await StartHostAsync();

        // The flat is delivered FIRST -- its closing fill has not been ingested yet. This is the adverse order
        // gh#748 names, not a race: the fill is armed only after the early flat's outcome is observed below.
        running.Stream.Arm(FlatAt("8101", At(2)));

        bool earlyOutcomeRecorded = await WaitUntilAsync(
            () => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));
        earlyOutcomeRecorded.Should().BeTrue("the early flat must be observed and recorded, never silently dropped");

        // The closing fill lands SECOND, through the real ingestion path (gh#911's own requirement: hand-writing
        // the Fill row would assume away the delivery-order hazard under test).
        running.Stream.Arm(ExitFill("8101", "exit-8101", "exit-fill-8101", OrderSide.Sell, 4_980m, At(2)));

        bool composed = await WaitUntilAsync(() => QueryDbAsync(database =>
            database.Trades.IgnoreQueryFilters().AnyAsync(trade => trade.AccountId == accountId)));
        composed.Should().BeTrue(
            "the fill landing after the flat must trigger a retry that composes the round trip -- this is the "
            + "defer-then-retry path gh#748 adds, not a same-pass compose");

        await running.StopAsync();

        List<Trade> trades = await TradesAsync(accountId);
        trades.Should().ContainSingle(
            "exactly one round trip closed here -- a second row would double-count into the daily governor");
        trades[0].RealizedPnL.Should().Be(
            -100m, "buy @5000, sell @4980, 1 lot at a point value of 5 -- a 20-point loss, signed");
        trades[0].OpeningFillId.Should().Be(entryFill, "paired with the only entry fill this window ever had");
        trades[0].ClosingFillId.Should().NotBeNull("keyed on the fill that arrived after the flat that closed it");
    }

    // =================================================================================================================
    // AC2 — Deferred, then journalled: the outcome sequence is `deferred` (early flat) -> `journalled` (fill-
    // triggered retry), never `not-composable` for this window.
    // =================================================================================================================

    [Fact(Skip =
        "blocked by gh#748 -- the 'deferred' outcome does not exist in ExecutionMetrics today, and the early flat "
        + "records 'not-composable' instead (see the DEFECT-pinned case in this class). Flips green once gh#748 "
        + "lands and the Skip is removed.")]
    public async Task AccountEventStreamHost_ShouldRecordDeferredThenJournalled_WhenTheFlatArrivesBeforeItsClosingFill()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "8102");

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-8102", [(FillId(2, 0x01), 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-8102", []);

        await using RunningHost running = await StartHostAsync();

        running.Stream.Arm(FlatAt("8102", At(2)));
        bool earlyOutcomeRecorded = await WaitUntilAsync(
            () => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));
        earlyOutcomeRecorded.Should().BeTrue();

        running.Stream.Arm(ExitFill("8102", "exit-8102", "exit-fill-8102", OrderSide.Sell, 4_980m, At(2)));
        bool composed = await WaitUntilAsync(() => QueryDbAsync(database =>
            database.Trades.IgnoreQueryFilters().AnyAsync(trade => trade.AccountId == accountId)));
        composed.Should().BeTrue();

        await running.StopAsync();

        List<string?> outcomes = [.. _factory.Capture.For(ExecutionMetrics.JournalOutcomes)
            .Select(measurement => (string?)measurement.Tags.GetValueOrDefault("outcome"))];

        outcomes.Should().Equal(
            ["deferred", ExecutionMetrics.JournalWritten],
            "the early flat must record 'deferred' -- never 'not-composable', which is the discriminator's "
            + "TERMINAL branch for genuine ambiguity, not an incomplete window -- and the fill-triggered retry "
            + "must then record 'journalled'; pre-#748 this sequence was 'not-composable' with nothing after it");
    }

    // =================================================================================================================
    // AC3 — Idempotent on retry: once the trade composes, a replayed flat writes no second row -- the real unique
    // index backstops the retry, not just the in-memory pre-check.
    // =================================================================================================================

    [Fact(Skip =
        "blocked by gh#748 -- this case depends on AC1's compose happening first, which cannot happen on develop "
        + "today. Flips green once gh#748 lands and the Skip is removed.")]
    public async Task AccountEventStreamHost_ShouldWriteNoSecondRow_WhenTheComposedTradeIsReplayed()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "8103");

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-8103", [(FillId(3, 0x01), 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-8103", []);

        await using RunningHost running = await StartHostAsync();

        running.Stream.Arm(FlatAt("8103", At(2)));
        await WaitUntilAsync(() => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));

        running.Stream.Arm(ExitFill("8103", "exit-8103", "exit-fill-8103", OrderSide.Sell, 4_980m, At(2)));
        bool composed = await WaitUntilAsync(() => QueryDbAsync(database =>
            database.Trades.IgnoreQueryFilters().AnyAsync(trade => trade.AccountId == accountId)));
        composed.Should().BeTrue("the precondition -- AC1's compose -- must hold before the replay means anything");

        List<Trade> afterCompose = await TradesAsync(accountId);
        afterCompose.Should().ContainSingle();
        Guid survivingId = afterCompose[0].Id;

        _factory.Capture.Clear();

        // At-least-once redelivery of the SAME flat, well after the trade already composed -- the retry's own
        // idempotency, not the original compose's.
        running.Stream.Arm(FlatAt("8103", At(2)));

        bool replayOutcomeRecorded = await WaitUntilAsync(
            () => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));
        replayOutcomeRecorded.Should().BeTrue("the replay must be observed, not silently no-op'd with nothing to see");

        await running.StopAsync();

        List<Trade> afterReplay = await TradesAsync(accountId);
        afterReplay.Should().ContainSingle(
            "a replayed flat recomposes the SAME leg, whose key is the same row -- a second row here is a "
            + "double-count straight into the R-4 / R-5 daily governor, and is exactly what the composite "
            + "(ClosingFillId, OpeningFillId) unique index exists to backstop even if the in-memory pre-check "
            + "somehow missed it");
        afterReplay[0].Id.Should().Be(survivingId, "the surviving row is the ORIGINAL one, untouched by the replay");
    }

    // =================================================================================================================
    // AC4 — Genuine ambiguity still refuses: a same-instant opposite-side tie window still records `not-composable`
    // and writes no row, and is NOT left parked deferred. Provable TODAY: unaffected by whether a defer mechanism
    // exists, so this is a real regression guard both before and after gh#748.
    // =================================================================================================================

    [Fact]
    public async Task AccountEventStreamHost_ShouldRefuseAndNeverDefer_WhenASameInstantOppositeSideTieFallsInsideTheWindow()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedTiedWindowAsync(owner, "8104", block: 4);

        await using RunningHost running = await StartHostAsync();

        running.Stream.Arm(FlatAt("8104", At(2)));

        bool outcomeRecorded = await WaitUntilAsync(
            () => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));
        outcomeRecorded.Should().BeTrue("an ambiguous window must still be observed, not silently ignored");

        await running.StopAsync();

        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().ContainSingle(
            measurement => (string?)measurement.Tags.GetValueOrDefault("outcome") == ExecutionMetrics.JournalNotComposable,
            "a genuine same-instant opposite-side tie has no single right answer and must record 'not-composable' "
            + "-- the discriminator's TERMINAL branch");
        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().NotContain(
            measurement => (string?)measurement.Tags.GetValueOrDefault("outcome") == "deferred",
            "a genuine ambiguity is not an incomplete window -- retrying it on a later fill would only ever "
            + "reproduce the same tie, so it must never be parked as a retryable 'deferred'");

        (await TradesAsync(accountId)).Should().BeEmpty(
            "a row written here would carry a side, an entry and an exit chosen by nothing but Guid order, and "
            + "its P&L would enter the daily governor with a sign that could just as easily have been the other one");
    }

    // =================================================================================================================
    // AC5 / prove-red — DEFECT gh#748 (QA contract rule 2): pins the OBSERVED pre-fix behaviour so the suite
    // documents reality without enshrining it. This IS the "would fail on the pre-#748 code" proof gh#911 asks
    // for; because gh#748 is unimplemented on develop today, there is nothing to locally revert -- this case runs
    // against the actual pre-#748 code as it stands. Delete it once gh#748 lands; its own failure at that point is
    // the fix's own regression signal, and AC1 above (Skip removed) becomes its replacement.
    // =================================================================================================================

    [Fact]
    public async Task AccountEventStreamHost_ShouldPermanentlyLoseTheRoundTrip_WhenTheFlatArrivesBeforeItsClosingFill_PreGh748()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "8199");

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-8199", [(FillId(99, 0x01), 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-8199", []);

        await using RunningHost running = await StartHostAsync();

        running.Stream.Arm(FlatAt("8199", At(2)));

        bool earlyOutcomeRecorded = await WaitUntilAsync(
            () => Task.FromResult(_factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any()));
        earlyOutcomeRecorded.Should().BeTrue();

        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().ContainSingle(
            measurement => (string?)measurement.Tags.GetValueOrDefault("outcome") == ExecutionMetrics.JournalNotComposable,
            "DEFECT gh#748: pre-fix, the writer can only see the entry fill when the flat arrives -- the window "
            + "never balances, and the early flat records 'not-composable', indistinguishable from a genuine "
            + "ambiguity it is not");

        running.Stream.Arm(ExitFill("8199", "exit-8199", "exit-fill-8199", OrderSide.Sell, 4_980m, At(2)));

        bool fillLanded = await WaitUntilAsync(() => QueryDbAsync(database => database.Fills
            .IgnoreQueryFilters()
            .AnyAsync(fill => fill.VenueFillKey == "exit-fill-8199")));
        fillLanded.Should().BeTrue(
            "the closing fill must reach real ingestion before the defect can be observed -- otherwise this proves "
            + "nothing about the retry gap and only that the fill never arrived");

        // Give the (nonexistent) retry every reasonable chance to fire before concluding it never will.
        bool everComposed = await WaitUntilAsync(
            () => QueryDbAsync(database =>
                database.Trades.IgnoreQueryFilters().AnyAsync(trade => trade.AccountId == accountId)),
            attempts: 20,
            delayMs: 50);

        await running.StopAsync();

        everComposed.Should().BeFalse(
            "DEFECT gh#748: AccountEventIngestionService's FillEvent branch only writes the Fill row -- it never "
            + "re-invokes TradeJournalService, so the round trip closed here is lost forever, exactly as gh#748 "
            + "describes ('the round trip is then permanently lost ... nothing re-composes')");
        (await TradesAsync(accountId)).Should().BeEmpty("DEFECT gh#748: confirmed lost -- no Trade ever composed");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static DateTimeOffset At(int minutes) => _origin.AddMinutes(minutes);

    private static Guid FillId(int block, byte order) =>
        Guid.Parse($"{block:x8}-0000-0000-0000-0000000000{order:x2}");

    private static PositionEvent FlatAt(string venueAccountKey, DateTimeOffset at) =>
        new(VenueAccountId.Create(_projectx, venueAccountKey), at,
            VenueContractId.Create(_projectx, Contract), NetQuantity: 0, new Price(5_000m));

    private static FillEvent ExitFill(
        string venueAccountKey,
        string venueOrderKey,
        string venueFillKey,
        OrderSide side,
        decimal price,
        DateTimeOffset at) =>
        new(VenueAccountId.Create(_projectx, venueAccountKey), at, venueOrderKey, venueFillKey, side,
            Quantity: 1, new Price(price), Fees: 0m, Voided: false);

    /// <summary>
    /// Starts a fresh, isolated <see cref="AccountEventStreamHost"/> against this fixture's stream double --
    /// resetting the channel and clearing captured measurements first, so one test's events and metrics can never
    /// leak into another's on this shared-container fixture.
    /// </summary>
    private async Task<RunningHost> StartHostAsync()
    {
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>().MakeAccountStreamingSupported();
        TestAccountEventStream stream = _factory.Services.GetRequiredService<TestAccountEventStream>();
        stream.Reset();
        _factory.Capture.Clear();

        AccountEventStreamHost host = new(
            _factory.Services, _factory.Services.GetRequiredService<ILogger<AccountEventStreamHost>>());
        await host.StartAsync(CancellationToken.None);
        return new RunningHost(host, stream);
    }

    /// <summary>A started host plus its stream double, torn down with <see cref="StopAsync"/> or on dispose --
    /// the same start/try/finally-stop shape the sibling suites use, wrapped so each case does not repeat it.</summary>
    private sealed class RunningHost(AccountEventStreamHost host, TestAccountEventStream stream) : IAsyncDisposable
    {
        public TestAccountEventStream Stream { get; } = stream;

        private readonly AccountEventStreamHost _host = host;
        private bool _stopped;

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            await _host.StopAsync(CancellationToken.None);
            _stopped = true;
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    /// <summary>Seeds the ambiguous window AC4 needs: a buy at <c>At(0)</c>, an opposite-side pair both at
    /// <c>At(1)</c>, and a sell at <c>At(2)</c> -- the same shape <c>TradeFifoPairingIntegrationTests</c> uses for
    /// its direct-call ambiguity cases (gh#809, ADR-0022), driven here through the real host instead.</summary>
    private async Task<Guid> SeedTiedWindowAsync(Guid owner, string venueKey, int block)
    {
        Guid accountId = await SeedAccountAsync(owner, venueKey);

        Guid entry = FillId(block, 0x01);
        Guid tiedSell = FillId(block, 0x10);
        Guid tiedBuy = FillId(block, 0x20);
        Guid finalExit = FillId(block, 0x30);

        DateTimeOffset tie = At(1);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"entry-{venueKey}", [(entry, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, $"tied-sell-{venueKey}", [(tiedSell, 5_010m, 1, tie)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"tied-buy-{venueKey}", [(tiedBuy, 5_010m, 1, tie)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, $"exit-{venueKey}", [(finalExit, 5_020m, 1, At(2))]);

        return accountId;
    }

    /// <summary>One discoverable practice account for <paramref name="owner"/>, under that operator's single firm
    /// and its single projectx connection -- created on first call for an owner, reused on later ones (the same
    /// shape <c>TradeFifoPairingIntegrationTests.SeedAccountAsync</c> documents at length).</summary>
    private async Task<Guid> SeedAccountAsync(Guid owner, string venueAccountKey)
    {
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async database =>
        {
            Connection? existing = await database.Connections
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(connection => connection.UserId == owner
                    && connection.Platform == "projectx"
                    && connection.CredentialKey == OurCredentialKey);

            Guid connectionId = existing?.Id ?? Guid.NewGuid();

            if (existing is null)
            {
                Guid firmId = Guid.NewGuid();
                database.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
                database.Connections.Add(new Connection
                {
                    Id = connectionId,
                    UserId = owner,
                    FirmId = firmId,
                    Platform = "projectx",
                    CredentialKey = OurCredentialKey,
                });
            }

            database.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = venueAccountKey,
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            await database.SaveChangesAsync();
        });

        return accountId;
    }

    /// <summary>Seeds an executed order and its fills with EXPLICIT fill ids. An empty <paramref name="fills"/>
    /// seeds a still-working order whose executions the venue has not reported yet -- the shape a late/deferred
    /// <see cref="FillEvent"/> resolves against.</summary>
    private async Task SeedOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        string venueOrderKey,
        (Guid Id, decimal Price, int Size, DateTimeOffset At)[] fills)
    {
        Guid orderId = Guid.NewGuid();

        await ExecuteDbAsync(async database =>
        {
            database.Orders.Add(new Order
            {
                Id = orderId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = side,
                Size = fills.Length == 0 ? 1 : fills.Sum(fill => fill.Size),
                Type = OrderType.Market,
                EntryPrice = fills.Length == 0 ? 5_000m : fills[0].Price,
                PointValue = ContractPointValue,
                TickSize = 0.25m,
                Status = fills.Length == 0 ? OrderStatus.Working : OrderStatus.Filled,
                Mode = TradingMode.Practice,
                VenueOrderKey = venueOrderKey,
                PlacedAt = fills.Length == 0 ? _origin : fills[0].At,
            });

            foreach ((Guid id, decimal price, int size, DateTimeOffset at) in fills)
            {
                database.Fills.Add(new Fill
                {
                    Id = id,
                    UserId = owner,
                    OrderId = orderId,
                    VenueFillKey = id.ToString("N"),
                    Price = price,
                    Size = size,
                    ExecutedAt = at,
                });
            }

            await database.SaveChangesAsync();
        });
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> stage)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await stage(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    /// <summary>Scoped to ONE account: the fixture is a single container shared by the whole class.</summary>
    private async Task<List<Trade>> TradesAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Trades
            .IgnoreQueryFilters()
            .Where(trade => trade.AccountId == accountId)
            .OrderBy(trade => trade.ClosedAt)
            .ToListAsync();
    }

    /// <summary>Polls until <paramref name="condition"/> holds, returning <see langword="false"/> on timeout rather
    /// than throwing -- the shape the sibling stream-driven suites use, so a genuine red reports as an assertion
    /// failure instead of an unhandled exception.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 200, int delayMs = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }
}
