using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent integration coverage for the <b>synthetic conditional-entry engine</b> — arm a pending conditional,
/// let a quote cross its trigger, and watch the firing watcher fire it through the <b>authoritative fire-time
/// re-gate</b> (gh#180, pairing #198's watcher; ADR-0007; R-11; R-5 / R-16 / R-12). Runs against real PostgreSQL
/// with the applied <c>AddConditionalOrders</c> migration, so its DB-level guards (the <c>CK_ConditionalOrders_*</c>
/// checks) are live in every run — invisible to any in-memory provider.
/// </summary>
/// <remarks>
/// <para>
/// Written from the requirement, not the implementation (QA contract §2). The unit suite
/// (<c>Api/MarketData/ConditionalFiringServiceTests</c>) proves the firing <i>logic</i> in memory against a fake
/// venue; this proves the <b>storage + gate + venue seam end-to-end</b>: the create endpoint persists a pending
/// conditional and transmits nothing, the watcher's <see cref="ConditionalFiringService.ProcessQuoteAsync"/> fires
/// / cancels / expires it through the real <c>OrderExecutionService</c> gate, and — the safety property this suite
/// exists for — a conditional valid when armed but violating the gate at fire time is <b>refused, not sent</b>.
/// </para>
/// <para>
/// The firing watcher is the event log's second consumer (<see cref="ConditionalOrderHost"/>). Most tests drive
/// the consumer's core (<see cref="ConditionalFiringService.ProcessQuoteAsync"/>) directly with a controlled clock
/// — exactly what the host does after decoding a <c>market.quote</c> event — so the fire/cancel/expire decisions
/// are deterministic. One test (<see cref="FiringWatcher_ShouldConsumeAMarketQuoteEvent_AndFireThroughTheGate"/>)
/// appends a real quote to <see cref="IEventLog"/> and lets the <b>live background host</b> consume it, proving the
/// #198 wiring (log → decode → fire) end-to-end.
/// </para>
/// <para>
/// <b>Isolation.</b> The container is shared across the class (one host, one DB), so every test uses its own
/// account (a distinct firm) <b>and its own instrument</b> — the watcher discovers pending conditionals by
/// <c>Instrument == contractKey</c>, so a distinct contract key per test keeps one test's
/// <see cref="ConditionalFiringService.ProcessQuoteAsync"/> from touching another's leftover pending rows. Placement
/// counts are asserted as before/after <b>deltas</b> because the venue-stub factory is a shared singleton.
/// </para>
/// <para>
/// The venue seam is the sole sanctioned double (QA contract). It is adversarial: it reports
/// <see cref="TradingMode.Live"/> for every account regardless of stage and never hands back the gate's decision —
/// it only feeds the roster / positions / resolved contract and records what was transmitted, so nothing here can
/// pass by the stub returning the production-computed answer.
/// </para>
/// </remarks>
public class ConditionalFiringIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed clock for the deterministic direct-drive fires — firing does not read a wall clock.</summary>
    private static DateTimeOffset Now { get; } = new(2026, 7, 24, 15, 0, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public ConditionalFiringIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // 1. Arming — persists Pending, transmits nothing.
    // =============================================================================================================

    [Fact]
    public async Task Arm_ShouldPersistPendingConditional_AndTransmitNothing()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondArm");
        await DeclareRiskProfileAsync(client, accountId);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MES", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        // The guard this test exists for: arming holds the proposal local — it never reaches the venue.
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "a synthetic conditional is held local and shown at the broker only when it fires");

        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);

            record.Status.Should().Be(ConditionalStatus.Pending, "a freshly armed conditional rests Pending");
            record.FiredOrderId.Should().BeNull("nothing has fired, so no real order is linked yet");
            record.Instrument.Should().Be(contractKey);
            record.Mode.Should().Be(TradingMode.Practice, "the mode mirrors the account, not the venue's Live claim");

            // The proposal is carried WHOLE — the watcher rebuilds and re-gates it from these exact fields (R-12).
            record.Symbol.Should().Be("MES");
            record.Side.Should().Be(OrderSide.Buy);
            record.Size.Should().Be(1);
            record.EntryPrice.Should().Be(5_000m);
            record.WorkingStopPrice.Should().Be(4_990m);
            record.SafetyStopPrice.Should().Be(4_980m);
            record.ReferencePrice.Should().Be(5_000m);
            record.TickSize.Should().Be(0.25m);
            record.PointValue.Should().Be(5m);
            record.TriggerPrice.Should().Be(5_010m);
            record.TriggerDirection.Should().Be(ConditionalCrossDirection.RisesTo);

            // Nothing execution-shaped exists yet: no journaled order, no staged stop, no gate decision.
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId))
                .Should().BeFalse("arming journals no order");
            (await db.GateDecisions.IgnoreQueryFilters().AnyAsync(d => d.AccountId == accountId))
                .Should().BeFalse("arming records no gate decision — the authoritative gate runs at fire time");
        });
    }

    [Fact]
    public async Task Arm_ShouldReject_WhenCancelBandSitsOnTheWrongSideOfTheTrigger()
    {
        // The API surfaces the domain aggregate's invariant: a RisesTo order's cancel band must sit BELOW the
        // trigger (the stale side). A band above it would cancel the instant price approached the trigger — the
        // opposite of what a drift-cancel is for. Refused at creation, so nothing incoherent is ever held.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondArmBadBand");
        await DeclareRiskProfileAsync(client, accountId);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        CreateConditionalOrderRequest request = new(
            Order: ValidProposal("MNQ"),
            TriggerPrice: 5_010m,
            TriggerDirection: ConditionalCrossDirection.RisesTo,
            CancelDrift: 5_020m); // ABOVE the trigger — the wrong side for RisesTo

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a wrong-side cancel band is refused at creation");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "a rejected arm never reaches the venue");

        await ExecuteDbContextAsync(async db =>
            (await db.ConditionalOrders.IgnoreQueryFilters().AnyAsync(c => c.AccountId == accountId))
                .Should().BeFalse("a rejected conditional is not persisted"));
    }

    // =============================================================================================================
    // 2. Firing — a crossing quote fires the conditional to a working native order.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldTransmitAWorkingNativeOrder_AndJournalStopPlanAndDecision_WhenTheTriggerCrosses()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondFire");
        await DeclareRiskProfileAsync(client, accountId);

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MYM", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        // A long transacts on the ask; ask 5010 >= the 5010 RisesTo trigger, so the trigger crosses.
        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        acted.Should().Be(1, "exactly one pending conditional fired");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore + 1, "firing transmits the entry to the venue exactly once");

        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(ConditionalStatus.Fired, "the crossed trigger fires the synthetic order");
            record.FiredOrderId.Should().NotBeNull("firing links the real order it became");

            Order journaled = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.AccountId == accountId);
            journaled.Id.Should().Be(record.FiredOrderId!.Value, "the linked id is the journaled working order");
            journaled.Status.Should().Be(OrderStatus.Working, "the fired entry rests as a working native order");
            journaled.VenueOrderKey.Should().NotBeNullOrWhiteSpace("the venue's own order handle is recorded");
            journaled.Mode.Should().Be(TradingMode.Practice, "the journaled mode mirrors the account");
            journaled.Instrument.Should().Be(contractKey);

            // The fired position is protected: a hidden stop plan is staged (so the stop-promotion watcher owns it).
            (await db.StopPlans.IgnoreQueryFilters().CountAsync(p => p.OrderId == journaled.Id))
                .Should().Be(1, "firing stages the working stop for the stop-promotion watcher");

            // The fire-time decision is audited — a single approving decision for the journaled order.
            List<GateDecisionRecord> decisions = await db.GateDecisions
                .IgnoreQueryFilters().Where(d => d.AccountId == accountId).ToListAsync();
            decisions.Should().ContainSingle("the fire-time gate decision is audited");
            decisions[0].OrderId.Should().Be(journaled.Id);
            decisions[0].ApprovedQuantity.Should().Be(1, "$20 of safety-stop distance x $5/point = $100, inside the $300 cap");
        });
    }

    [Fact]
    public async Task Fire_ShouldNotTransmit_WhenTheTriggerHasNotCrossed()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondNoFire");
        await DeclareRiskProfileAsync(client, accountId);

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "M2K", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        // Ask 5005 is below the 5010 RisesTo trigger — nothing to fire.
        int acted = await FireQuoteAsync(contractKey, bid: 5_004m, ask: 5_005m, Now);

        acted.Should().Be(0, "no conditional was acted on");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "an un-crossed trigger transmits nothing");

        await ExecuteDbContextAsync(async db =>
        {
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Pending, "it rests until its trigger actually crosses");
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId))
                .Should().BeFalse("nothing was journaled");
        });
    }

    [Fact]
    public async Task FiringWatcher_ShouldConsumeAMarketQuoteEvent_AndFireThroughTheGate()
    {
        // END-TO-END WIRING (gh#198): the conditional-order host is the event log's second consumer. This appends a
        // real `market.quote` event to the backbone and lets the LIVE background host consume it — decode → fire —
        // proving the watcher is wired to the log, not just that its core method works. Isolated by a unique
        // instrument so the host's ProcessQuoteAsync touches only this conditional; polled because a background
        // consumer is asynchronous.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondWatcher");
        await DeclareRiskProfileAsync(client, accountId);

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MHG", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        // The shape QuoteIngestionService writes: contract as the venue-qualified `venue:key`, and a crossing ask.
        await AppendQuoteEventAsync(contractKey, bid: 5_009m, ask: 5_010m);

        ConditionalOrderRecord fired = await WaitForStatusAsync(
            conditionalId, ConditionalStatus.Fired, TimeSpan.FromSeconds(30));

        fired.FiredOrderId.Should().NotBeNull("the watcher fired the conditional into a real order");

        await ExecuteDbContextAsync(async db =>
        {
            Order journaled = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == fired.FiredOrderId!.Value);
            journaled.Status.Should().Be(OrderStatus.Working, "the watcher transmitted a working native order");
            journaled.VenueOrderKey.Should().NotBeNullOrWhiteSpace("the venue handle proves the entry reached the venue");
            journaled.AccountId.Should().Be(accountId);
        });
    }

    // =============================================================================================================
    // 3. The authoritative fire-time re-gate — R-5 / R-16 / R-12 re-checked at fire time, not only at arm time.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldRefuseAndStayPending_WhenRiskTightenedBetweenArmAndFire()
    {
        // THE safety property of the whole engine (R-12): the arm-time evaluation is feedback, not authorization.
        // Firing is a NEW entry that re-runs the authoritative gate against whatever the limits say NOW. A
        // conditional that passed when armed but violates the gate at fire time must be REFUSED, never transmitted —
        // and, unlike a rejected send, it is not lost: it stays Pending and re-decides on the next quote.
        //
        // Proven by moving only the limits between arm and fire: arm passes at a $300 per-trade cap, then the cap
        // is tightened to $50 (the same proposal's ~$100 risk no longer fits). If the re-gate ran only at arm time,
        // the crossing quote would fire and this test would go red on the placement delta.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondRegate");
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 300m);

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MGC", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        // Tighten below the armed proposal's risk. The pending row is untouched; only the limits moved.
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 50m);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        acted.Should().Be(0, "a gate-refused fire acts on nothing — the conditional is neither fired nor resolved");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "a fire the fire-time gate refuses must NEVER reach the venue");

        await ExecuteDbContextAsync(async db =>
        {
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Pending, "a refused fire is not lost — it re-decides next quote");
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId))
                .Should().BeFalse("nothing was transmitted, so nothing was journaled");

            // The refusal is auditable — the fire-time gate ran and blocked (approved zero contracts).
            List<GateDecisionRecord> decisions = await db.GateDecisions
                .IgnoreQueryFilters().Where(d => d.AccountId == accountId).ToListAsync();
            decisions.Should().ContainSingle("the blocking fire-time decision is recorded");
            decisions[0].OrderId.Should().BeNull("a blocked fire journals no order to attach the decision to");
            decisions[0].ApprovedQuantity.Should().Be(0, "the tightened cap authorized zero contracts");
        });
    }

    // =============================================================================================================
    // 4. Cancel-if (drift) and expiry — a stale / expired pending conditional is cancelled / expired, never fired.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldCancel_OnAnAdverseDriftPastTheCancelBand()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondCancel");
        await DeclareRiskProfileAsync(client, accountId);

        // RisesTo 5010 with a cancel band at 4950 (the stale side, below the trigger).
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MCL", trigger: 5_010m, ConditionalCrossDirection.RisesTo, cancelDrift: 4_950m);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        // Price drifted away to the band (ask 4950 <= 4950) without ever crossing the trigger: abandon the setup.
        int acted = await FireQuoteAsync(contractKey, bid: 4_949m, ask: 4_950m, Now);

        acted.Should().Be(1, "the drift-cancel acted on the conditional");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "a cancelled conditional never transmits");

        await ExecuteDbContextAsync(async db =>
        {
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Cancelled, "an adverse drift past the band abandons the setup");
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId)).Should().BeFalse();
        });
    }

    [Fact]
    public async Task Fire_ShouldExpire_RatherThanFire_WhenTheValidityWindowHasPassed()
    {
        // Expiry outranks firing: a conditional whose validity window has passed must expire even as its trigger
        // crosses on the same quote — a stale setup must never fire. If expiry were checked after firing (or not at
        // all), the crossing quote below would transmit and this test would go red on the status and the placement.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondExpire");
        await DeclareRiskProfileAsync(client, accountId);

        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1); // valid at arm time
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MBT", trigger: 5_010m, ConditionalCrossDirection.RisesTo, expiresAt: expiry);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        // Fire clock is past the deadline AND the ask crosses the trigger: expiry must win.
        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, expiry.AddMinutes(1));

        acted.Should().Be(1, "the expiry acted on the conditional");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "an expired conditional never transmits");

        await ExecuteDbContextAsync(async db =>
        {
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Expired, "past its window it expires rather than fires");
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId)).Should().BeFalse();
        });
    }

    // =============================================================================================================
    // 5. Full lifecycle — each transition one-way and idempotent; the DB is the last line for the invariants.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldBeIdempotent_AndNeverReFireAResolvedConditional()
    {
        // At-least-once redelivery (ADR-0001): a replayed quote must not double-transmit. Firing is Pending-only, so
        // once resolved to Fired a conditional is never rediscovered — re-processing the same crossing quote is a
        // no-op. If firing re-fired a resolved order, the second pass would transmit again and this goes red.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondIdempotent");
        await DeclareRiskProfileAsync(client, accountId);

        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, symbol: "MET", trigger: 5_010m, ConditionalCrossDirection.RisesTo);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        int firstActed = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);
        firstActed.Should().Be(1, "the first crossing quote fires it");

        Guid? firedOrderIdNullable = await ExecuteDbContextQueryAsync(db => db.ConditionalOrders
            .IgnoreQueryFilters().Where(c => c.Id == conditionalId).Select(c => c.FiredOrderId).SingleAsync());
        firedOrderIdNullable.Should().NotBeNull("the first fire linked a real order");
        Guid firedOrderId = firedOrderIdNullable!.Value;
        int placementsAfterFire = VenueFactory.TotalPlacedOrderCount;
        placementsAfterFire.Should().Be(placementsBefore + 1, "the first fire transmits exactly once");

        // Replay the identical crossing quote.
        int secondActed = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        secondActed.Should().Be(0, "a resolved conditional is Pending-only — it is never rediscovered");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsAfterFire, "re-processing must never double-transmit an already-fired conditional");

        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(ConditionalStatus.Fired, "the resolved state is one-way — it stays Fired");
            record.FiredOrderId.Should().Be(firedOrderId, "the linked order is unchanged — no second order was minted");

            (await db.Orders.IgnoreQueryFilters().CountAsync(o => o.AccountId == accountId))
                .Should().Be(1, "the replay minted no second order");
            (await db.StopPlans.IgnoreQueryFilters().CountAsync(p => p.OrderId == firedOrderId))
                .Should().Be(1, "the replay staged no second stop plan");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectCancelDriftOnTheWrongSide_ViaTheAppliedCheckConstraint()
    {
        // The DB is the last line, below the API. `CK_ConditionalOrders_CancelDrift_StaleSide` from the applied
        // migration refuses a band on the near side of the trigger even when written straight through the context —
        // proving the guard the endpoint enforces above is ALSO enforced by the database. Invisible to any
        // in-memory provider; only a real Postgres run witnesses it.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondDbBand");

        await ExecuteDbContextAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);

            ConditionalOrderRecord bad = RawConditional(account);
            bad.TriggerDirection = ConditionalCrossDirection.RisesTo;
            bad.TriggerPrice = 5_010m;
            bad.CancelDriftPrice = 5_020m; // ABOVE the trigger — the wrong side for RisesTo
            db.ConditionalOrders.Add(bad);

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_ConditionalOrders_CancelDrift_StaleSide"
                        || error.MessageText.Contains("CK_ConditionalOrders_CancelDrift_StaleSide", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownTriggerDirection_ViaTheAppliedCheckConstraint()
    {
        // The fail-closed companion: `CK_ConditionalOrders_Direction_NotUnknown`. Unknown never fires (it is the
        // unset value), so the database refuses it outright — a conditional that could rest forever must never be
        // stored (R-11, ADR-0007).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-CondDbDirection");

        await ExecuteDbContextAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);

            ConditionalOrderRecord bad = RawConditional(account);
            bad.TriggerDirection = ConditionalCrossDirection.Unknown; // the refusable zero
            db.ConditionalOrders.Add(bad);

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_ConditionalOrders_Direction_NotUnknown"
                        || error.MessageText.Contains("CK_ConditionalOrders_Direction_NotUnknown", StringComparison.Ordinal)));
        });
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Drives the firing watcher's core exactly as <see cref="ConditionalOrderHost"/> does after decoding
    /// a quote — real DB, real gate, adversarial venue — with a caller-supplied clock so decisions are deterministic.</summary>
    private async Task<int> FireQuoteAsync(string contractKey, decimal bid, decimal ask, DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ConditionalFiringService firing = scope.ServiceProvider.GetRequiredService<ConditionalFiringService>();
        return await firing.ProcessQuoteAsync(contractKey, bid, ask, now, CancellationToken.None);
    }

    /// <summary>Appends a real <c>market.quote</c> event to the backbone, in the shape the ingester writes.</summary>
    private async Task AppendQuoteEventAsync(string contractKey, decimal bid, decimal ask)
    {
        string payload = JsonSerializer.Serialize(new { contract = $"projectx:{contractKey}", bid, ask });
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        await log.AppendAsync(
            new EventDraft(QuoteIngestionService.QuoteEventType, "projectx", DateTimeOffset.UtcNow, payload),
            CancellationToken.None);
    }

    /// <summary>Polls the conditional until it reaches <paramref name="target"/> or the timeout elapses.</summary>
    private async Task<ConditionalOrderRecord> WaitForStatusAsync(
        Guid conditionalId, ConditionalStatus target, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        ConditionalOrderRecord? record = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            record = await ExecuteDbContextQueryAsync(db => db.ConditionalOrders
                .IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == conditionalId));
            if (record is not null && record.Status == target)
            {
                return record;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        // Timed out: assert with a clear message (the background host did not fire within the window).
        record.Should().NotBeNull("the conditional must still exist");
        record!.Status.Should().Be(
            target,
            $"the firing watcher should have consumed the quote event and reached {target} within {timeout.TotalSeconds:0}s");
        return record;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered tradeable account.</summary>
    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId, decimal maxDrawdownPerTrade = 300m)
    {
        DeclareRiskProfileRequest declareReq = new(
            DailyLossLimit: 1_000m,
            AccountProfitTarget: 3_000m,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 2_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: maxDrawdownPerTrade,
            DailyDrawdownGovernor: 600m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Arms a conditional via the create endpoint and returns its id and the resolved contract key.</summary>
    private async Task<(Guid Id, string ContractKey)> ArmConditionalAsync(
        HttpClient client,
        Guid accountId,
        string symbol,
        decimal trigger,
        ConditionalCrossDirection direction,
        decimal? cancelDrift = null,
        DateTimeOffset? expiresAt = null,
        int quantity = 1)
    {
        CreateConditionalOrderRequest request = new(
            Order: ValidProposal(symbol, quantity),
            TriggerPrice: trigger,
            TriggerDirection: direction,
            CancelDrift: cancelDrift,
            ExpiresAt: expiresAt);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's conditional must arm cleanly");
        ConditionalOrderResponse? armed = await response.Content.ReadFromJsonAsync<ConditionalOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(armed);
        armed.Status.Should().Be(nameof(ConditionalStatus.Pending), "an armed conditional is Pending");

        string contractKey = await ExecuteDbContextQueryAsync(db => db.ConditionalOrders
            .IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == armed.ConditionalOrderId)
            .Select(c => c.Instrument)
            .SingleAsync());

        return (armed.ConditionalOrderId, contractKey);
    }

    private static SendOrderRequest ValidProposal(string symbol, int quantity = 1) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: quantity,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);

    /// <summary>A gate-independent, DB-valid pending conditional for the check-constraint tests to mutate.</summary>
    private static ConditionalOrderRecord RawConditional(Account account) => new()
    {
        Id = Guid.NewGuid(),
        UserId = account.UserId,
        AccountId = account.Id,
        Instrument = "MESM25",
        Symbol = "MES",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        EntryPrice = 5_000m,
        WorkingStopPrice = 4_990m,
        SafetyStopPrice = 4_980m,
        ReferencePrice = 5_000m,
        TickSize = 0.25m,
        PointValue = 5m,
        TriggerPrice = 5_010m,
        TriggerDirection = ConditionalCrossDirection.RisesTo,
        Status = ConditionalStatus.Pending,
        Mode = TradingMode.Practice,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> ExecuteDbContextQueryAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
