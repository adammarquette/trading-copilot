using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the <b>suggestion take, part A</b> (gh#614 ⇒ gh#548; R-4 / R-11b / R-12 / R-5 /
/// R-16 / R-20, ADR-0007 / ADR-0013): <c>POST /suggestions/{id}/take</c> — the first path that turns a suggestion
/// into an order. Authored from the spec (the gh#548 issue body, PRD R-11b/R-12 and the ADRs), <b>not</b> from the
/// coding implementation (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline claim.</b> A take <i>arms</i> and transmits <b>nothing</b> — sending stays the separate, fully
/// gated <c>POST /orders/{id}/take</c>. So the load-bearing assertion on every case below is
/// <see cref="AdversarialTestProjectXVenueFactory.TotalPlacedOrderCount"/> <b>unmoved</b> across the request. It is
/// snapshotted before the action and compared after, never asserted as zero: placements are cumulative for the life
/// of the class fixture and <c>ResetPositions()</c> does not clear them.
/// </para>
/// <para>
/// <b>What only a container-backed run can see.</b> The unit tier is EF InMemory, which enforces no unique index,
/// no CHECK, no FK action and no constraint trigger. Everything here that matters — the
/// <c>IX_SuggestionDispositions_SuggestionId</c> unique backstop under a genuine two-connection race, the
/// per-account <c>pg_advisory_lock</c> that serializes two concurrent takes, the R-20 query filter reaching an HTTP
/// 404, and <c>CK_StopPlans_SafetyBeyondActual</c> being <b>pre-empted in code</b> rather than met at the database —
/// needs real PostgreSQL with the shipped migrations applied.
/// </para>
/// <para>
/// <b>The venue seam is the sole sanctioned double</b> and it is adversarial: it reports a fixed roster it claims is
/// <see cref="TradingMode.Live"/> (the mode the take actually runs under is the one the <i>domain</i> recomputed
/// from the firm's conventions), it resolves a contract key the suggestion never carries, and it never computes any
/// answer under test. Its <c>PlaceOrderAsync</c> is a counter this suite reads as a witness that the arm never
/// reached it. Hosts are suppressed (<see cref="OcoExitTestPostgresFactory"/>, the sanctioned reuse other suites
/// already make of it purely for host suppression) so no background pass perturbs the seeded surface or the venue
/// counters.
/// </para>
/// <para>
/// <b>Instrument.</b> Everything is seeded on <b>CL</b>, whose built-in spec (gh#541) is tick <c>0.01</c>, point
/// value <c>1,000</c>, safety-stop <c>50</c> ticks. That fixes the derived catastrophic floor at <c>0.50</c> from
/// entry and the R-12 drift band at <c>8 × 0.01 = 0.08</c>, and keeps one contract's notional inside the sanity cap
/// (ES at a realistic index level exhausts <c>MaxNotional</c> by itself, which would refuse before any take-specific
/// rung was reached). <b>MES</b> — deliberately absent from the built-in specs — is the unconfigured-instrument case.
/// </para>
/// </remarks>
public class SuggestionTakeIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    // The venue-NEUTRAL symbol, never a contract key: the take resolves the contract server-side (the adversarial
    // venue answers "CLM25"), so seeding a contract key would miss the spec and refuse for the wrong reason.
    private const string Instrument = "CL";
    private const string UnconfiguredInstrument = "MES";

    private const decimal Entry = 70.00m;
    private const decimal SafetyFloorLong = 69.50m;  // entry - 50 ticks * 0.01
    private const decimal WorkingStopLong = 69.75m;  // inside the floor, below entry
    private const decimal TargetLong = 70.50m;       // the winning side, per CK_Orders_TakeProfit_WinningSide
    // Suggestions:DriftToleranceTicks (8) × CL's 0.01 tick. The offsets in the drift theory are chosen against THIS
    // instrument on purpose: 0.09 sits inside ES's 2.00 band and outside CL's, so a band that stopped being derived
    // per-instrument would move the boundary rows rather than leave them coincidentally green.
    private const decimal DriftBand = 0.08m;

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    // The refusal bodies on these routes are anonymous `{ error = "..." }` objects — there is no ProblemDetails
    // anywhere here, and deserializing into one would yield nulls and an assertion that can never fail.
    private sealed record ErrorResponse(string? Error);

    public SuggestionTakeIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // 1. The arm: a Staged ticket, stamped with its provenance, and NOTHING transmitted.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldArmAStagedTicketAndTransmitNothing_WhenTheSuggestionIsStillTakeable()
    {
        // THE guard of gh#548: the take stages an editable ticket and reaches no venue order path at all. It goes
        // red the moment the arm starts transmitting — if the handler called SendAsync instead of Evaluate, or a
        // future "one-action take" quietly routed through here, TotalPlacedOrderCount moves and the venue hands
        // back an order key. It also pins the two facts the 200 body cannot carry (StagedOrderResponse echoes
        // neither the suggestion id nor the row's size): Order.SuggestionId is stamped, and the entry method
        // records ArmedTake so a reader can tell a reviewed take from a manual ticket (R-11).
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        int idsBefore = VenueFactory.PlacedVenueOrderIds.Count;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        staged.Status.Should().Be(nameof(OrderStatus.Staged), "a take arms; it does not send");

        Order armed = await OrderForSuggestionAsync(suggestionId);
        armed.Id.Should().Be(staged.OrderId);
        armed.Status.Should().Be(OrderStatus.Staged);
        armed.SuggestionId.Should().Be(suggestionId, "the arm stamps its provenance, so a take is traceable end to end (gh#548)");
        armed.EntryMethod.Should().Be(OrderEntryMethod.ArmedTake, "how the order entered the journal is a fact of R-11");
        armed.Symbol.Should().Be(Instrument, "the row keeps the neutral symbol the suggestion carried");
        armed.Instrument.Should().Be($"{Instrument}M25", "the CONTRACT is resolved server-side by the venue, never taken from the suggestion");
        armed.VenueOrderKey.Should().BeNull("nothing rests at the venue — the ticket still requires an explicit, separately gated send");

        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "an arm transmits nothing (gh#548, ADR-0007)");

        // Scoped to THIS request, not asserted absolutely. PlacedVenueOrderIds is cumulative across the class's
        // shared fixture, so `BeEmpty()` only held while no test in the file ever transmitted — it was passing on
        // the absence of a sibling rather than on the arm's inertness, and the positive control below (which does
        // transmit) turned it red the moment it was added. A guard that depends on its neighbours is not a guard.
        idsBefore.Should().Be(VenueFactory.PlacedVenueOrderIds.Count, "the arm minted no venue order id");
    }

    [Fact]
    public async Task ArmedTicket_ShouldReachTheVenue_OnlyWhenItIsExplicitlySent()
    {
        // THE POSITIVE CONTROL for every "transmits nothing" assertion in this suite — and, as it turns out, for the
        // whole integration project: before this test, *no* suite anywhere asserted TotalPlacedOrderCount ever
        // INCREASES. Every use pinned it at zero or unmoved. A counter that silently stopped incrementing — a
        // renamed seam, a double that stopped recording, a factory that handed back a fresh venue per scope — would
        // leave all of those green while proving nothing at all, which is precisely the vacuous guard the QA
        // contract's rule 1 exists to forbid.
        //
        // So: arm through the take (must not transmit), then send the armed ticket explicitly (must transmit). The
        // SAME counter must stay still for one and move for the other, in one test, or the distinction this suite
        // is built to prove is unobservable.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage armed = await TakeAsync(client, suggestionId, Entry);
        armed.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(armed));
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "arming reaches no venue — the half this suite asserts everywhere else");

        Order ticket = await OrderForSuggestionAsync(suggestionId);
        using HttpResponseMessage sent = await client.PostAsync($"/orders/{ticket.Id}/take", content: null);

        sent.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(sent));
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore + 1,
            "the explicit send DOES reach the venue — without this, 'transmits nothing' could hold because the "
            + "counter is broken rather than because the arm is inert");
        VenueFactory.PlacedVenueOrderIds.Should().NotBeEmpty("a transmitted order mints a venue order id");
    }

    [Fact]
    public async Task Take_ShouldWriteNoStopPlan_WhenTheWorkingStopRestsExactlyOnTheCatastrophicFloor()
    {
        // CK_StopPlans_SafetyBeyondActual is a STRICT ordering (safety < actual < entry for a long), and the take's
        // in-code guard is deliberately looser — it permits a COINCIDENT stop (safety == actual), which stages no
        // hidden stop at all. Those two rules can only coexist because an armed ticket writes NO StopPlan row: the
        // plan is written by the send path, once there is a position to protect. So this case is the sharpest
        // possible statement of "take writes no plan" — the very geometry the DB would refuse arms cleanly, and if
        // the take ever started writing its plan at arm time it would fail on the constraint rather than 200 here.
        // Its anti-vacuity partner is StopPlan_ShouldStillRefuseACoincidentSafetyStop_AtTheDatabase below.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, stop: SafetyFloorLong);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
        Order armed = await OrderForSuggestionAsync(suggestionId);
        armed.SafetyStopPrice.Should().Be(SafetyFloorLong, "the floor is DERIVED from the server-side spec, never client-supplied");
        armed.WorkingStopPrice.Should().Be(SafetyFloorLong, "the coincident stop is what makes this the constraint's boundary case");

        (await StopPlanCountForAsync(armed.Id)).Should().Be(
            0, "an armed ticket writes no StopPlan — which is the only reason a coincident safety stop is representable");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "and it still transmitted nothing");
    }

    [Fact]
    public async Task StopPlan_ShouldStillRefuseACoincidentSafetyStop_AtTheDatabase()
    {
        // The anti-vacuity control for the case above: without it, "no StopPlan row" would also read as proven if
        // CK_StopPlans_SafetyBeyondActual had been dropped from the migrations — the take would arm cleanly either
        // way. Writing the armed ticket's own geometry straight to StopPlans proves the constraint is live and
        // strict, so the take path genuinely must pre-empt it in code rather than lean on the database.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, stop: SafetyFloorLong);
        HttpClient client = await AuthenticatedClientAsync();
        using (HttpResponseMessage armResponse = await TakeAsync(client, suggestionId, Entry))
        {
            armResponse.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(armResponse));
        }

        Order armed = await OrderForSuggestionAsync(suggestionId);

        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = armed.UserId,
                OrderId = armed.Id,
                Side = OrderSide.Buy,
                EntryPrice = Entry,
                ActualStopPrice = SafetyFloorLong,
                SafetyStopPrice = SafetyFloorLong, // coincident — the take permits it, the CHECK does not
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = StopStaging.Hidden,
            });

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_StopPlans_SafetyBeyondActual"
                        || error.MessageText.Contains("CK_StopPlans_SafetyBeyondActual", StringComparison.Ordinal)));
        });
    }

    // =============================================================================================================
    // 2. Size is the suggestion's — never the gate's number.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldStageTheSuggestionsSize_EvenWhenTheGateResizesTheDecision()
    {
        // The armed row carries the SUGGESTION's size; the gate's resize binds at SEND, not on the ticket the
        // operator is about to review and edit (ADR-0007). The fixture's manual cap (MaxContractsPerOrder = 1)
        // makes the gate authorize strictly fewer contracts than the suggestion asks for, so the two numbers are
        // forced apart and an implementation that staged decision.ApprovedQuantity instead would go red here.
        // Reading only the 200 body could not catch it — StagedOrderResponse exposes ApprovedQuantity (the gate's
        // number) and never the row's Size, so the row must be read from Postgres.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, size: 3);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        staged.Outcome.Should().Be(nameof(GateOutcome.Resized), "the fixture is built so the gate genuinely wants fewer contracts");
        staged.ApprovedQuantity.Should().Be(1, "the gate's authorization is the manual cap, and it rides out on the response");
        staged.BindingLayer.Should().Be(RiskLayer.ManualCap, "which layer bound is part of the operator's answer (R-5)");

        Order armed = await OrderForSuggestionAsync(suggestionId);
        armed.Size.Should().Be(3, "the STAGED row carries the suggestion's size — the gate's resize binds at send, not on the armed ticket");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "a resized arm is still an arm — nothing transmitted");
    }

    // =============================================================================================================
    // 3. One disposition per suggestion — take and pass are coupled, and the unique index is the backstop.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldConflict_WhenTheSuggestionWasAlreadyPassed()
    {
        // The disposition unique index is UNFILTERED and therefore kind-blind: a Passed row occupies the one slot a
        // later Taken row would need. The endpoint pre-checks so the operator gets a clean 409 rather than a 500
        // from the index, and the coupling is real rather than incidental — a take that ignored dispositions would
        // arm here and leave a suggestion that was both passed and taken.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient client = await AuthenticatedClientAsync();

        using (HttpResponseMessage pass = await client.PostAsJsonAsync(
            $"/suggestions/{suggestionId}/pass", new SuggestionPassRequest(SuggestionPassReason.WrongTime)))
        {
            pass.StatusCode.Should().Be(HttpStatusCode.OK, "the arrange step must actually record a disposition");
        }

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            "disposition", "the 409 must name the disposition — this route returns 409 for three distinct causes");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
        (await DispositionCountAsync(suggestionId)).Should().Be(1, "the refused take added no second disposition");
    }

    [Fact]
    public async Task Take_ShouldArmExactlyOneTicket_WhenTwoTakesRaceTheSameSuggestion()
    {
        // The anti-double-arm check runs TWICE: once unsynchronized (explicitly "a courtesy refusal, not the
        // guard") and authoritatively inside the per-account pg_advisory_lock, immediately before the insert. Only
        // a genuinely concurrent pair of HTTP takes — two clients, two DI scopes, two Postgres connections —
        // exercises the second one. If the lock were removed, or the re-check hoisted back outside it, both racers
        // would read "no live order" before either committed and mint two Staged tickets off one suggestion, which
        // the send-vs-take gap could then transmit as double the intended risk (gh#589).
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient first = await AuthenticatedClientAsync();
        HttpClient second = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        HttpResponseMessage[] responses = await Task.WhenAll(
            TakeAsync(first, suggestionId, Entry),
            TakeAsync(second, suggestionId, Entry));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(
                1, "exactly one racer may arm — the other is serialized behind it and must see its committed row");

            HttpResponseMessage loser = responses.Single(response => response.StatusCode != HttpStatusCode.OK);
            loser.StatusCode.Should().Be(HttpStatusCode.Conflict, await DescribeAsync(loser));
            (await ErrorOfAsync(loser)).Should().Contain(
                "live order", "the loser's refusal is the anti-stacking guard, not a disposition or pre-gate 409");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        (await OrderCountForSuggestionAsync(suggestionId)).Should().Be(
            1, "one suggestion, one armed ticket — the durable outcome, whichever racer won");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "and neither racer transmitted anything");
    }

    [Fact]
    public async Task Pass_ShouldLeaveExactlyOneDisposition_WhenTwoPassesRaceTheSameSuggestion()
    {
        // The take path's clean 409 rests on there never being two dispositions to disagree about, and the
        // endpoint's pre-check alone cannot promise that: it is an unsynchronized read, so two racers can both
        // answer "none yet" before either has saved. IX_SuggestionDispositions_SuggestionId is what actually makes
        // "one disposition per suggestion" true, and only real Postgres enforces it (EF InMemory has no unique
        // indexes, so the unit tier would happily store both). Drop the index and this test finds two rows.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient first = await AuthenticatedClientAsync();
        HttpClient second = await AuthenticatedClientAsync();

        HttpResponseMessage[] responses = await Task.WhenAll(
            first.PostAsJsonAsync($"/suggestions/{suggestionId}/pass", new SuggestionPassRequest(SuggestionPassReason.NewsRisk)),
            second.PostAsJsonAsync($"/suggestions/{suggestionId}/pass", new SuggestionPassRequest(SuggestionPassReason.LowConviction)));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(
                1, "at most one pass may be recorded, and the arrange must not have failed both");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        (await DispositionCountAsync(suggestionId)).Should().Be(
            1, "the unique index is the backstop that makes the journal append-only under a real race");

        // And the coupling holds afterwards: whichever pass won, the take is now refused.
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        using HttpResponseMessage take = await TakeAsync(first, suggestionId, Entry);
        take.StatusCode.Should().Be(HttpStatusCode.Conflict, await DescribeAsync(take));
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
    }

    // =============================================================================================================
    // 4. The stop-ordering guard — CK_StopPlans_SafetyBeyondActual pre-empted IN CODE, because no plan row is
    //    written at arm and the risk gate checks stops-below-entry but not safety-beyond-working.
    // =============================================================================================================

    [Theory]
    [InlineData(OrderSide.Buy, 69.00, "a long whose stop sits BEYOND the catastrophic floor — the floor would fire first")]
    [InlineData(OrderSide.Buy, 70.00, "a long whose stop sits AT the entry — no losing-side protection at all")]
    [InlineData(OrderSide.Sell, 71.00, "a short whose stop sits beyond its floor — the mirrored half of the ordering")]
    [InlineData(OrderSide.Sell, 70.00, "a short whose stop sits at the entry")]
    public async Task Take_ShouldRefuse_WhenTheStopIsNotBetweenEntryAndTheCatastrophicFloor(
        OrderSide side, double stop, string because)
    {
        // The ONLY enforcement of this ordering on the take path. The risk gate checks that stops sit on the losing
        // side of entry but NOT that the safety stop rests beyond the working one, and an armed ticket writes no
        // StopPlan, so CK_StopPlans_SafetyBeyondActual never sees it. Relax the in-code guard (widen the comparison
        // or drop a side's arm) and every row here arms instead of refusing — the inversion ships silently, and the
        // native bracket would fire tighter than the operator's own stop. Both sides and both boundary shapes are
        // exercised: a one-sided or one-shaped guard passes half of them.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(
            accountId, operatorId, side: side, stop: (decimal)stop, target: side == OrderSide.Buy ? TargetLong : 69.50m);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, because + " — " + await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            "safety floor", "the refusal must be the ordering guard, not a drift, spec or gate refusal wearing the same 422");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
    }

    [Fact]
    public async Task Take_ShouldArm_WhenTheStopSitsStrictlyInsideTheFloor_SoTheOrderingGuardIsNotRefusingEverything()
    {
        // The anti-vacuity control for the theory above: without it, every row there would also pass if the take
        // refused all CL suggestions for some unrelated reason — a suite that proves the endpoint broken rather
        // than the ordering guard working.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, stop: WorkingStopLong);
        HttpClient client = await AuthenticatedClientAsync();

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
    }

    // =============================================================================================================
    // 5. R-20 tenancy — the DbContext query filter, reaching HTTP as a 404 that discloses nothing.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldReturn404AndArmNothing_WhenAnotherOperatorAttemptsIt()
    {
        // Default-deny at the data layer (ADR-0017): a second operator's request simply cannot see the row, so the
        // handler's own "not found" branch answers — a 404 with no body, never a 403 or a detail that would confirm
        // the id is real. If the handler ever reached for IgnoreQueryFilters (as background plumbing legitimately
        // does), a stranger would arm an order against someone else's account, and this goes red on the status
        // code AND on the durable surface: no order, no disposition, on either side.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient owner = await AuthenticatedClientAsync();
        HttpClient intruder = await CreateSecondOperatorAsync(owner);
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage byIntruder = await TakeAsync(intruder, suggestionId, Entry);

        byIntruder.StatusCode.Should().Be(HttpStatusCode.NotFound, await DescribeAsync(byIntruder));
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
        (await DispositionCountAsync(suggestionId)).Should().Be(0, "a stranger's take dispositions nothing either");

        // Positive control: the owner CAN take it, so the 404 above is isolation rather than an unusable fixture.
        using HttpResponseMessage byOwner = await TakeAsync(owner, suggestionId, Entry);
        byOwner.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(byOwner));
        (await OrderForSuggestionAsync(suggestionId)).UserId.Should().Be(operatorId, "the armed ticket belongs to the owner");
    }

    // =============================================================================================================
    // 6. The synchronous R-12 refusals — each one refused NOW, with its own reason, before anything is staged.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldReturn404_WhenTheSuggestionDoesNotExist()
    {
        // An unknown id is indistinguishable from another operator's — the same bare 404, so a probe cannot tell
        // "no such row" from "not yours".
        await FreshAccountAsync();
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, Guid.NewGuid(), Entry);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await DescribeAsync(response));
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "an unknown id reaches no venue path");
    }

    [Fact]
    public async Task Take_ShouldRefuse_WhenTheValidityWindowHasPassedButTheStoredStateIsStillActive()
    {
        // The sharp half of R-12: the expiry sweep and the drift consumer are both EVENTUALLY consistent, so a
        // suggestion can sit stored as Active with its window already gone. The take re-reads the CLOCK rather than
        // trusting the stored state, which is the only thing that closes the sweep's lag window. An implementation
        // that checked `State == Active` alone would arm this expired setup, and this test is what catches it.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid suggestionId = await SeedSuggestionAsync(
            accountId,
            operatorId,
            state: SuggestionState.Active,
            createdAt: now.AddHours(-2),
            expiresAt: now.AddMinutes(-1)); // already due; no sweep has run in this host (hosted services are stripped)
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            "validity window", "a window that passed is a different reason from a state that moved — the operator is told which");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
        (await StateOfAsync(suggestionId)).Should().Be(
            SuggestionState.Active, "the take refuses without mutating lifecycle state — that is the sweep's job (gh#539)");
    }

    [Theory]
    [InlineData(SuggestionState.Stale, "a setup the market has drifted away from is not chased (ADR-0013)")]
    [InlineData(SuggestionState.ExpiredVoid, "a terminal suggestion is never resurrected into an order")]
    public async Task Take_ShouldRefuse_WhenTheSuggestionIsNoLongerActive(SuggestionState state, string because)
    {
        // The forward-only lifecycle, enforced at the one path that turns a suggestion into an order. Widening the
        // takeable set to "anything not ExpiredVoid" (or dropping the state check entirely) arms a stale setup at a
        // level the market has already left, and these rows go red.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, state: state);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, because + " — " + await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            state.ToString(), "the refusal names the state the operator is looking at");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
    }

    [Theory]
    [InlineData(0.09, false, "one tick past the band, above")]
    [InlineData(-0.09, false, "one tick past the band, below — drift is symmetric, unlike a conditional's directional band")]
    [InlineData(0.08, true, "exactly AT the band: the boundary is exclusive, matching SuggestionLifecycle.HasDrifted")]
    [InlineData(0.07, true, "one tick inside the band")]
    public async Task Take_ShouldRefuse_WhenTheReferenceHasDriftedPastTheEntryTolerance(
        double offset, bool takeable, string because)
    {
        // THE synchronous drift re-check (R-12): the drift consumer is eventually consistent, so the take
        // re-measures the caller's current reference against entry ± (DriftToleranceTicks × the INSTRUMENT's tick
        // size) at the moment of the take. Widening the tolerance — the prove-the-red the card names — flips the
        // two refusing rows to 200. The two accepting rows are the other direction of the same guard: they pin the
        // exclusive boundary, so a tolerance that silently narrowed (or a `>` that became `>=`) is caught too, and
        // they stop the refusing rows passing vacuously.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        decimal reference = Entry + (decimal)offset;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, reference);

        if (takeable)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, because + " — " + await DescribeAsync(response));
            (await OrderForSuggestionAsync(suggestionId)).ReferencePrice.Should().Be(
                reference, "the caller's reference is what the fat-finger band was measured from (R-16)");
            VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "an arm inside the band still transmits nothing");
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, because + " — " + await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            "drifted", "the refusal must be the drift re-check, not the ordering or spec guard sharing this 422");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);

    }

    [Fact]
    public async Task Take_ShouldRefuse_WhenNoContractSpecIsConfiguredForTheInstrument()
    {
        // Fail-closed on the three numbers a server-originated proposal cannot invent (gh#541). MES is deliberately
        // absent from the built-in specs, and nothing here configures it. A substituted or defaulted tick size
        // would not fail loudly — it would silently mis-size every downstream risk calculation and derive a
        // catastrophic floor at the wrong distance, so the only safe answer is a refusal.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, instrument: UnconfiguredInstrument);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, Entry);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain(
            UnconfiguredInstrument, "the refusal names the instrument that has no spec, so the operator can configure it");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
    }

    [Theory]
    [InlineData(0.0, "a zero reference is not a price — and would read as an enormous drift rather than a refusal")]
    [InlineData(-1.0, "a negative reference is not a price either")]
    public async Task Take_ShouldRefuse_WhenTheReferencePriceIsNotPositive(double reference, string because)
    {
        // The reference is caller-supplied on every order path (the server fetches no venue quote), so it must
        // actually be one. Dropping this guard does not merely admit a bad number: a zero passed on to the drift
        // re-check reads as a huge move and would refuse for the wrong reason, while a negative one reaches the
        // fat-finger band as a nonsense distance.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId);
        HttpClient client = await AuthenticatedClientAsync();
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await TakeAsync(client, suggestionId, (decimal)reference);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because + " — " + await DescribeAsync(response));
        (await ErrorOfAsync(response)).Should().Contain("reference price");
        await AssertNothingArmedAsync(suggestionId, placementsBefore);
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    /// <summary>The adversarial venue double — always resolved from the same host that served the request, so the
    /// "nothing transmitted" assertions can never pass vacuously against a different container's singleton.</summary>
    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private static Task<HttpResponseMessage> TakeAsync(HttpClient client, Guid suggestionId, decimal referencePrice) =>
        client.PostAsJsonAsync($"/suggestions/{suggestionId}/take", new SuggestionTakeRequest(referencePrice));

    /// <summary>The two facts every refusal must leave behind: no ticket, and no venue contact.</summary>
    private async Task AssertNothingArmedAsync(Guid suggestionId, int placementsBefore)
    {
        (await OrderCountForSuggestionAsync(suggestionId)).Should().Be(0, "a refused take stages nothing");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placementsBefore, "and a refused take reaches no venue order path");
    }

    private async Task<string> ErrorOfAsync(HttpResponseMessage response)
    {
        ErrorResponse? body = await response.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOptions);
        body?.Error.Should().NotBeNullOrWhiteSpace("every refusal on this route carries an { error } body");
        return body?.Error ?? string.Empty;
    }

    /// <summary>The body, for an assertion message — a bare status-code mismatch hides which rung of the ladder fired.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"the response body was: {await response.Content.ReadAsStringAsync()}";

    private Task<Order> OrderForSuggestionAsync(Guid suggestionId) => QueryDbAsync(db => db.Orders
        .IgnoreQueryFilters().AsNoTracking().SingleAsync(order => order.SuggestionId == suggestionId));

    private Task<int> OrderCountForSuggestionAsync(Guid suggestionId) => QueryDbAsync(db => db.Orders
        .IgnoreQueryFilters().CountAsync(order => order.SuggestionId == suggestionId));

    private Task<int> StopPlanCountForAsync(Guid orderId) => QueryDbAsync(db => db.StopPlans
        .IgnoreQueryFilters().CountAsync(plan => plan.OrderId == orderId));

    private Task<int> DispositionCountAsync(Guid suggestionId) => QueryDbAsync(db => db.SuggestionDispositions
        .IgnoreQueryFilters().CountAsync(disposition => disposition.SuggestionId == suggestionId));

    private Task<SuggestionState> StateOfAsync(Guid suggestionId) => QueryDbAsync(db => db.Suggestions
        .IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == suggestionId).Select(s => s.State).SingleAsync());

    private async Task<Guid> SeedSuggestionAsync(
        Guid accountId,
        Guid operatorId,
        OrderSide side = OrderSide.Buy,
        int size = 1,
        decimal? stop = null,
        decimal? target = null,
        string instrument = Instrument,
        SuggestionState state = SuggestionState.Active,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset created = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Id = id,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = instrument, // the neutral SYMBOL — the take resolves the contract itself
                Side = side,
                Size = size,
                EntryPrice = Entry,
                StopPrice = stop ?? (side == OrderSide.Buy ? WorkingStopLong : 70.25m),
                TargetPrice = target ?? (side == OrderSide.Buy ? TargetLong : 69.50m),
                Mode = TradingMode.Practice, // must equal the account's, or ct_suggestions_mode_matches_account raises
                State = state,
                CreatedAt = created,
                // Required since gh#542/#543/#544; ExpiresAt must clear CK_Suggestions_ExpiresAfterCreated, so it
                // sits strictly after CreatedAt.
                Rationale = "seeded for the take-path suite (gh#614)",
                CitedFactors =
                [
                    new CitedFactor
                    {
                        Id = Guid.NewGuid(),
                        UserId = operatorId,
                        Kind = CitedFactorKind.Indicator,
                        IsPrimary = true,
                        TimeframeMinutes = 5,
                        Indicator = "rsi",
                        Period = 14,
                    },
                ],
                Confidence = 60,
                ExpiresAt = expiresAt ?? created.AddHours(1),
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>
    /// Wipes the decision surface, then stands up one <b>Practice</b> account through the real onboarding path
    /// (firm → conventions with no capital at risk → connection on the process's credential key → discover) and
    /// declares the risk profile the shared composition ladder demands. The mode is the one the DOMAIN recomputed
    /// from conventions, never the <see cref="TradingMode.Live"/> the adversarial venue claims for every account.
    /// </summary>
    /// <remarks>
    /// The profile's numbers are chosen so the ladder's every other layer clears one CL contract (worst case
    /// 0.50 × $1,000 = $500) and the <b>manual cap</b> is the binding one at a single contract — which is what lets
    /// the resize case force the gate's number apart from the suggestion's size.
    /// </remarks>
    private async Task<(Guid AccountId, Guid OperatorId)> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.GateDecisions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.First(candidate => candidate.CanTrade);
        await DeclareRiskProfileAsync(client, account.Id);

        Guid operatorId = await QueryDbAsync(db => db.Users
            .AsNoTracking().Where(user => user.Email == PostgresApiFactory.OperatorEmail).Select(user => user.Id).SingleAsync());
        return (account.Id, operatorId);
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
            DailyLossLimit: 5_000m,
            AccountProfitTarget: 3_000m,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 10_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: 0.5m,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: 2_000m,
            DailyDrawdownGovernor: 4_000m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: false,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 1, // the deliberately binding layer — see the resize case
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
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

    /// <summary>A second, genuinely separate operator via the invitation flow — the only way to get a request whose
    /// R-20 filter is scoped to somebody else.</summary>
    private async Task<HttpClient> CreateSecondOperatorAsync(HttpClient owner)
    {
        string email = $"take-intruder-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "Intruder-Pass123!", "Take Intruder"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
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
