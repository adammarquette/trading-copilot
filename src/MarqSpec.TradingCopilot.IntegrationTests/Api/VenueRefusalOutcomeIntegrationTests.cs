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
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the <b>typed venue-refusal outcome</b> (gh#663, of gh#629 — safety-critical;
/// R-11 / R-12, ADR-0007's 2026-08-04 update, ADR-0013 §9) at <b>both</b> catch sites: the operator take
/// (<c>POST /orders/{id}/take</c>) and the conditional fire (<see cref="ConditionalFiringService"/>). Written from
/// the issue, not from the implementation (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one behaviour that matters.</b> A venue can refuse an order two ways, and they demand <i>opposite</i>
/// handling. A <b>definitive</b> rejection — the gateway answered in the negative and placed nothing — may resolve
/// the row automatically (take → <see cref="OrderStatus.Staged"/>, fire → <see cref="ConditionalStatus.Pending"/>).
/// An <b>indeterminate</b> fault — a timeout, a transport fault, an accepted-but-unidentified response, or anything
/// unrecognised — leaves an order that <b>may be resting live at the venue</b>, so the durable intent
/// (<see cref="OrderStatus.Taking"/> / <see cref="ConditionalStatus.Firing"/>) must be kept for a human or the
/// reconcile. Mis-classifying the second as the first releases a row whose order is live, and the next take / send /
/// fire then <b>stacks a second live order on it</b> — the hazard gh#530 / gh#589 exist to close. Every maybe-live
/// case here is therefore asserted explicitly, at both sites.
/// </para>
/// <para>
/// <b>Why this is a container-tier suite, and what only it can prove.</b> The unit tier proves the adapter's
/// classification and the catch-site branch against in-memory doubles. It cannot run what this does on real
/// PostgreSQL through <see cref="PostgresApiFactory"/> with the real migrations applied: the take path's release is
/// <see cref="IStagedOrderClaim"/>'s conditional <c>UPDATE … WHERE Status = Taking</c> — a genuine compare-and-swap
/// against a second connection (gh#530), which no in-memory provider can execute — and the fire path's
/// <c>Firing → Pending</c> revert is committed through the real per-record save and raced against the no-stacking
/// check under the Postgres advisory lock behind <see cref="IAccountEntryGuard"/> (gh#589).
/// </para>
/// <para>
/// <b>Guard discipline.</b> Every definitive case is paired with the same scenario differing in <b>one input</b> —
/// the refusal's <see cref="VenueRefusalKind"/> — and asserts the <i>opposite</i> outcome. The pair is mutually
/// exclusive by construction, so a catch site that ignores the kind (releases everything, or releases nothing)
/// necessarily reddens one half however it is written; no test here can pass both with and against the defect.
/// </para>
/// <para>
/// <b>The double is adversarial</b> (the sole sanctioned test double, QA contract §1). It <i>feeds</i> the fault the
/// test names and, on a refusal, mints <b>no</b> venue order id and returns nothing — so "nothing rests" is a
/// property of the seam, never an answer the stub hands the system. It never reports the row state, the claim, or
/// the firing decision; those are read back from the database.
/// </para>
/// <para>
/// <b>Not covered here.</b> The issue's staging sub-item — a genuinely-invalid order to the practice venue
/// returning <c>Success == false</c> and surfacing as <see cref="VenueRefusalKind.Definitive"/> — is venue-touching
/// and belongs to the post-merge staging tier; it is unrunnable pre-merge and is noted on the PR rather than faked
/// here. The adapter's <c>!success → Definitive</c> mapping is unit-covered (gh#629).
/// </para>
/// </remarks>
public class VenueRefusalOutcomeIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed clock for the deterministic direct-drive fires — firing does not read a wall clock.</summary>
    private static DateTimeOffset Now { get; } = new(2026, 8, 4, 15, 0, 0, TimeSpan.Zero);

    /// <summary>The venue key a second writer stamps when it adopts a row mid-take (the CAS case).</summary>
    private const string AdoptedVenueKey = "ADOPTED-BY-RECONCILE";

    private sealed record LoginTokenResponse(string Token);

    public VenueRefusalOutcomeIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The faults that leave an order <b>maybe-live</b> at the venue — every one of which must keep the durable
    /// intent. They are the failure surface of the mis-classification hazard: each is a distinct way the system can
    /// be tempted to conclude "nothing rests" when something might.
    /// </summary>
    public enum MaybeLiveFault
    {
        /// <summary>The venue accepted but returned no order id — it took the order, and there is no handle to it.</summary>
        IndeterminateRefusal,

        /// <summary>A refusal carrying <b>no</b> kind at all: the unset default must fail safe, not release.</summary>
        UnclassifiedRefusal,

        /// <summary>A venue timeout — a cancellation on the transport's own token, never the caller's.</summary>
        Timeout,

        /// <summary>A transport fault — the request may or may not have reached the gateway.</summary>
        Transport,

        /// <summary>An exception the catch site has never seen. Unrecognised must mean indeterminate.</summary>
        Unrecognised,
    }

    /// <summary>Every maybe-live fault, run against both catch sites.</summary>
    public static TheoryData<MaybeLiveFault> MaybeLiveFaults =>
    [
        MaybeLiveFault.IndeterminateRefusal,
        MaybeLiveFault.UnclassifiedRefusal,
        MaybeLiveFault.Timeout,
        MaybeLiveFault.Transport,
        MaybeLiveFault.Unrecognised,
    ];

    // =============================================================================================================
    // 1. The take path — POST /orders/{id}/take.
    // =============================================================================================================

    [Fact]
    public async Task Take_ShouldReleaseTheClaimToStaged_AndRestNothing_WhenTheVenueDefinitivelyRefuses()
    {
        // The definitive arm (gh#629): the gateway answered "!success" and placed NOTHING, so the claim taken before
        // the venue was touched (Staged -> Taking, gh#530) can be handed back and the operator can amend and retry.
        // The release is the claim's conditional UPDATE against real Postgres — a tracked-entity write would emit no
        // UPDATE at all here (the tracker still reads Staged), leaving the row stuck Taking, which is exactly what
        // this asserts against.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RefusalTakeDefinitive");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await ArmAsync(client, accountId, ValidProposal("MES"));

        VenueFactory.ClearPlaceOrderFaults();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;
        VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
            "ProjectX rejected the order: contract not tradable.", VenueRefusalKind.Definitive, errorCode: 4));

        using HttpResponseMessage response = await client.PostAsync($"/orders/{orderId}/take", null);

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict, "a definitively refused take is a conflict the operator can act on, not a 500");
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            "contract not tradable", "the venue's reason is surfaced, so the operator knows what to amend");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(
                OrderStatus.Staged, "nothing rests at the venue, so the ticket returns to the ladder re-takeable");
            order.VenueOrderKey.Should().BeNull("a refused transmit produced no venue handle");
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == orderId))
                .Should().BeFalse("no entry rests, so no protective stop was staged behind one");
        });

        VenueFactory.PlacedVenueOrderIds.Count.Should().Be(
            restingBefore, "the refused transmit minted no venue order — nothing rests to be stacked on");

        // "Re-takeable" is the claim, so prove it rather than reading the status as a promise: with the venue healthy
        // the SAME ticket takes cleanly. A row merely relabelled Staged while the claim still held would refuse here.
        VenueFactory.ClearPlaceOrderFaults();
        using HttpResponseMessage retake = await client.PostAsync($"/orders/{orderId}/take", null);

        retake.StatusCode.Should().Be(HttpStatusCode.OK, "a released ticket is genuinely takeable again");
        await ExecuteDbContextAsync(async db =>
            (await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId))
                .Status.Should().Be(OrderStatus.Working, "the retry transmitted and journaled normally"));
    }

    [Theory]
    [MemberData(nameof(MaybeLiveFaults))]
    public async Task Take_ShouldStrandTheRowTaking_AndRefuseARetake_WhenTheVenueFaultIsMaybeLive(MaybeLiveFault fault)
    {
        // THE mis-classification safety at the take site. Each fault below leaves an order that MAY be resting at the
        // venue; releasing the row would let the operator take it again and place a SECOND live order (gh#530). The
        // row must stay Taking — not re-takeable, blocking the account — until a human or POST /orders/{id}/reconcile
        // resolves it against venue truth (ADR-0013 §9). This is the paired red of the definitive case above: same
        // scenario, one input changed (the refusal's kind / the fault's type), opposite required outcome — so a catch
        // site that released everything, or that inferred "definitive" from anything but the adapter's own
        // classification, reddens here however it is written.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Topstep-RefusalTakeMaybeLive-{fault}");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await ArmAsync(client, accountId, ValidProposal("MES"));

        VenueFactory.ClearPlaceOrderFaults();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;
        VenueFactory.MakePlaceOrderThrow(() => Build(fault));

        HttpResponseMessage? response = await TakeIgnoringTransportFaultAsync(client, orderId);

        // The status code is deliberately NOT the guard: a maybe-live fault propagates out of the endpoint by design
        // (it is surfaced loudly, not converted into a tidy answer), and how the host renders that is not a safety
        // property. What must hold is that the caller was never told the take succeeded — and the row state below.
        (response?.IsSuccessStatusCode ?? false).Should().BeFalse(
            "a maybe-live venue fault must never be reported to the operator as a completed take");
        response?.Dispose();

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(
                OrderStatus.Taking,
                "the order MAY be live at the venue, so the durable claim is kept — releasing it re-opens the "
                + "gh#530 double-place, the one failure that puts a second live order on the account");
            order.VenueOrderKey.Should().BeNull("nothing was journaled — that is precisely why the row is stranded");
        });

        VenueFactory.PlacedVenueOrderIds.Count.Should().Be(
            restingBefore, "the double minted no venue id — the system cannot have learned the order's fate from it");

        // The strand must also BIND: a stranded Taking is not re-takeable (TryClaim requires Staged), which is what
        // keeps a second entry off a maybe-live order.
        int attemptsBefore = VenueFactory.TotalPlacedOrderCount;
        VenueFactory.ClearPlaceOrderFaults();
        using HttpResponseMessage retake = await client.PostAsync($"/orders/{orderId}/take", null);

        retake.StatusCode.Should().Be(HttpStatusCode.Conflict, "a stranded take cannot be re-taken");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            attemptsBefore, "the refused retake must not reach the venue at all");
    }

    [Fact]
    public async Task Take_ShouldNotReleaseARowAnotherWriterAdopted_WhenTheVenueDefinitivelyRefuses()
    {
        // The two-writers compare-and-swap the in-memory provider cannot run (gh#530), and the reason the release is
        // a conditional UPDATE rather than an assignment: while this take sits at the venue, a SECOND connection
        // resolves the same row — a reconcile adopting an order that did land, say — moving it Taking -> Working. The
        // definitive refusal that comes back must then release NOTHING: its `WHERE Status = Taking` matches no row.
        // An unconditional release would drag a row whose order is LIVE back to Staged and invite a second entry on
        // top of it. Only real Postgres arbitrates two connections, so only this tier can witness it.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RefusalTakeCas");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await ArmAsync(client, accountId, ValidProposal("MES"));

        VenueFactory.ClearPlaceOrderFaults();
        AdoptOnFirstTransmit(orderId);
        VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
            "ProjectX rejected the order: invalid size.", VenueRefusalKind.Definitive));

        using HttpResponseMessage response = await client.PostAsync($"/orders/{orderId}/take", null);

        VenueFactory.OnPlaceOrder(null);
        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict, "the take still reports the refusal it met — it cannot see the other writer");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(
                OrderStatus.Working,
                "the concurrent writer's adoption stands: a definitive refusal releases WHERE Status = Taking, so it "
                + "can never drag a row that has since become Working back into staging");
            order.VenueOrderKey.Should().Be(
                AdoptedVenueKey, "the adopted venue handle survives — the release wrote nothing over it");
        });

        VenueFactory.ClearPlaceOrderFaults();
    }

    // =============================================================================================================
    // 2. The fire path — the conditional-firing watcher's transmit.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldRevertToPending_AndJournalNothing_WhenTheVenueDefinitivelyRefuses()
    {
        // The fire site's definitive arm (gh#629, the gh#532 containment): the durable Firing intent (gh#577) commits
        // before the venue is touched, and a definitive rejection — nothing placed — reverts it to Pending so the
        // conditional is re-decided on the next quote rather than stranded for a human. The revert is committed
        // through the real per-record save against Postgres.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RefusalFireDefinitive");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(client, accountId, "MNQ", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;
        VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
            "ProjectX rejected the order: market closed.", VenueRefusalKind.Definitive, errorCode: 7));

        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        acted.Should().Be(0, "a definitively refused fire resolved nothing — the conditional is back where it was");

        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Pending, "nothing rests, so the intent reverts and the setup stays re-decidable");
            record.FiredOrderId.Should().BeNull("no order was placed, so none is linked");

            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId))
                .Should().BeFalse("a refused fire journals no order");
        });

        VenueFactory.PlacedVenueOrderIds.Count.Should().Be(
            restingBefore, "the refused transmit minted no venue order");

        // "Re-decidable" is the claim, so prove it: the very next crossing quote fires it for real.
        VenueFactory.ClearPlaceOrderFaults();
        int reacted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        reacted.Should().Be(1, "the reverted conditional is rediscovered — discovery is Pending-only");
        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(ConditionalStatus.Fired, "the retry fired normally");
            record.FiredOrderId.Should().NotBeNull("the fire journaled the order it became");
        });
    }

    [Theory]
    [MemberData(nameof(MaybeLiveFaults))]
    public async Task Fire_ShouldStayFiring_AndNeverReFire_WhenTheVenueFaultIsMaybeLive(MaybeLiveFault fault)
    {
        // THE mis-classification safety at the fire site, and the reason it is the more dangerous of the two: firing
        // is automatic. Reverting a maybe-live fire to Pending re-opens discovery (which is Pending-only), so the very
        // next quote would transmit a SECOND order on top of one that may already be resting — the double-transmit
        // gh#589's review caught. The conditional must stay Firing, which both blocks rediscovery and (counting Firing
        // in the no-stacking check) blocks an operator send / take from stacking on it.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Topstep-RefusalFireMaybeLive-{fault}");
        await DeclareRiskProfileAsync(client, accountId);
        // Its own instrument per case: the watcher discovers pending conditionals by contract key, so a distinct
        // symbol keeps one case's quote from touching another's leftover stranded row.
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(
            client, accountId, SymbolFor(fault), trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;
        VenueFactory.MakePlaceOrderThrow(() => Build(fault));

        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        acted.Should().Be(0, "a maybe-live fault resolves nothing — the pass presses on and surfaces it loudly");

        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Firing,
                "an order MAY be resting at the venue, so the durable intent is kept — reverting it to Pending would "
                + "let the next quote transmit a second order on top of a live one");
            record.FiredOrderId.Should().BeNull("nothing was journaled — that is what makes the row stranded");

            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId))
                .Should().BeFalse("nothing reached the journal");
        });

        VenueFactory.PlacedVenueOrderIds.Count.Should().Be(restingBefore, "the double minted no venue order");

        // The strand must BIND: with the venue healthy again, a second crossing quote must still not re-fire it.
        int attemptsBefore = VenueFactory.TotalPlacedOrderCount;
        VenueFactory.ClearPlaceOrderFaults();
        int reacted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

        reacted.Should().Be(0, "a stranded Firing conditional is never rediscovered — discovery is Pending-only");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            attemptsBefore, "the replayed quote must not transmit anything on top of a maybe-live order");

        await ExecuteDbContextAsync(async db =>
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Firing, "it stays stranded until a reconcile resolves it"));
    }

    // =============================================================================================================
    // 3. Concurrency — the definitive fire-revert against a peer entry on the same account.
    // =============================================================================================================

    [Fact]
    public async Task DefinitiveFireRevert_ShouldSerializeAgainstAConcurrentTake_AndNeverLetItStack()
    {
        // The definitive fire-revert races an operator take on the SAME account (gh#589 / gh#622). ComposeAsync sizes
        // as if flat and reserves nothing for an outstanding entry, so two entry-transmits in flight at once on one
        // account is up to twice the approved risk — IAccountEntryGuard's Postgres advisory lock is what prevents it,
        // and only a real database can arbitrate two connections.
        //
        // The peer take is launched from INSIDE the fire's transmit, so it arrives at the worst possible moment: the
        // conditional is durably Firing, the fire holds the account lock, and the refusal has not come back yet.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RefusalFireVsTake");
        await DeclareRiskProfileAsync(client, accountId);
        Guid stagedOrderId = await ArmAsync(client, accountId, ValidProposal("MES"));
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(client, accountId, "MYM", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        VenueFactory.ResetPlaceOrderCallSpans();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;

        // Only the FIRE is refused: it carries the conditional's id as its correlation handle (gh#577), the take
        // carries the order's (gh#589). A peer that transmits normally is the point — the guard must serialize two
        // healthy entries, not merely two failing ones.
        VenueFactory.MakePlaceOrderThrowFor(conditionalId.ToString(), () => new VenueRefusalException(
            "ProjectX rejected the order: order size exceeds the account limit.", VenueRefusalKind.Definitive));

        Task<HttpResponseMessage>? peerTake = null;
        bool peerFinishedInsideTheLock = false;
        bool launched = false;
        VenueFactory.OnPlaceOrder(async () =>
        {
            if (launched)
            {
                return;
            }

            launched = true;
            peerTake = client.PostAsync($"/orders/{stagedOrderId}/take", null);

            // Sampled, never awaited: awaiting here would deadlock on the account lock this fire holds — which is
            // itself the property under test, so it is observed instead of asserted inline (an assertion thrown here
            // would be swallowed by the fire's own maybe-live handler and the failure would be silent).
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            peerFinishedInsideTheLock = peerTake.IsCompleted;
        });

        int acted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);
        VenueFactory.OnPlaceOrder(null);

        peerTake.Should().NotBeNull("the interleave must have run — otherwise nothing was raced");
        using HttpResponseMessage peerResponse = await peerTake!;

        peerFinishedInsideTheLock.Should().BeFalse(
            "the peer take must WAIT on the account entry guard while the fire's transmit is in flight; if it ran "
            + "through, two entries composed the same flat-account snapshot and both could transmit");

        // Overlap is the direct witness of a broken lock: two transmits whose ordinal spans interleave were in flight
        // on the same account at once, each having composed the same flat snapshot.
        IReadOnlyList<(long Entered, long Left)> spans = VenueFactory.PlaceOrderCallSpans;
        for (int outer = 0; outer < spans.Count; outer++)
        {
            for (int inner = outer + 1; inner < spans.Count; inner++)
            {
                (spans[inner].Entered > spans[outer].Left || spans[outer].Entered > spans[inner].Left)
                    .Should().BeTrue(
                        "entry-transmits on one account must never overlap — spans {0} and {1} did",
                        spans[outer], spans[inner]);
            }
        }

        acted.Should().Be(0, "the definitively refused fire resolved nothing");

        // Whatever the peer's own outcome, the invariant is the same: the account never ends up holding two entries,
        // and the refused fire never left an order behind for the peer to stack on.
        await ExecuteDbContextAsync(async db =>
        {
            ConditionalOrderRecord record = await db.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Pending, "the definitive refusal reverted the durable intent — nothing rests");
            record.FiredOrderId.Should().BeNull("the refused fire journaled no order");

            (await db.Orders.IgnoreQueryFilters()
                .CountAsync(o => o.AccountId == accountId && o.Status == OrderStatus.Working))
                .Should().BeLessThanOrEqualTo(1, "an account may never end this race holding two live entries");
        });

        VenueFactory.PlacedVenueOrderIds.Count.Should().BeLessThanOrEqualTo(
            restingBefore + 1, "at most ONE of the two racing entries may rest at the venue");

        // The peer transmits — deterministically. The gh#622 over-block window this assertion used to tolerate is
        // CLOSED on this arm: the definitive fire-revert commits Firing -> Pending while the account lock is STILL
        // HELD (gh#629 / PR #667), the same discipline gh#630 established next door for the gate-refusal arm. There
        // is therefore no unlock -> outer-per-record-save gap in which a racer can acquire the lock and still read
        // Firing; the peer blocks on the advisory lock and, the instant it acquires, the no-stacking check (which
        // counts Firing — gh#589) reads a COMMITTED Pending and has nothing to refuse.
        //
        // So a 409 here is no longer a tolerable transient — it is the regression. Keeping Conflict on an allow-list
        // would let someone move that SaveChangesAsync back outside the lock, re-opening gh#622 on this arm, with the
        // suite still green (gh#674). Be(OK) is what makes this test able to fail on the thing it guards.
        peerResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the peer serializes behind the in-lock revert commit (gh#630 / gh#667) and transmits; a 409 here means "
            + "the revert commit escaped the account lock and re-opened the gh#622 over-block window");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == stagedOrderId);
            order.Status.Should().Be(OrderStatus.Working, "the peer entry lands exactly once, after the fire released");
            (await db.ConditionalOrders.IgnoreQueryFilters().SingleAsync(c => c.Id == conditionalId))
                .Status.Should().Be(ConditionalStatus.Pending, "the refused conditional stays re-decidable throughout");
        });

        VenueFactory.ClearPlaceOrderFaults();
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>
    /// Builds the fault the double feeds for a <see cref="MaybeLiveFault"/>. The timeout deliberately carries a
    /// cancellation token of its own — a venue timeout surfaces as a <see cref="TaskCanceledException"/> on the
    /// transport's token, <b>not</b> the caller's, so it is not the clean-shutdown case and must not be read as one.
    /// </summary>
    private static Exception Build(MaybeLiveFault fault) => fault switch
    {
        MaybeLiveFault.IndeterminateRefusal => new VenueRefusalException(
            "ProjectX accepted the order but returned no order id.", VenueRefusalKind.Indeterminate),
        MaybeLiveFault.UnclassifiedRefusal => new VenueRefusalException(
            "The venue refused the order, and said nothing about whether it placed it."),
        MaybeLiveFault.Timeout => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout elapsing.",
            new TimeoutException("A task was canceled."),
            new CancellationToken(canceled: true)),
        MaybeLiveFault.Transport => new HttpRequestException("The connection to the gateway was reset."),
        MaybeLiveFault.Unrecognised => new InvalidOperationException("Something the catch site has never seen."),
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unhandled maybe-live fault."),
    };

    /// <summary>A distinct instrument per theory case, so the fire cases cannot see each other's rows.</summary>
    private static string SymbolFor(MaybeLiveFault fault) => fault switch
    {
        MaybeLiveFault.IndeterminateRefusal => "MAA",
        MaybeLiveFault.UnclassifiedRefusal => "MAB",
        MaybeLiveFault.Timeout => "MAC",
        MaybeLiveFault.Transport => "MAD",
        MaybeLiveFault.Unrecognised => "MAE",
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unhandled maybe-live fault."),
    };

    /// <summary>
    /// Takes the ticket, tolerating a fault that propagates out of the endpoint. A maybe-live fault is re-thrown by
    /// design (it is surfaced loudly rather than converted into an answer), so the test host may raise it at the
    /// client instead of returning a response — either way the safety property is the row state, read separately.
    /// </summary>
    private static async Task<HttpResponseMessage?> TakeIgnoringTransportFaultAsync(HttpClient client, Guid orderId)
    {
        try
        {
            return await client.PostAsync($"/orders/{orderId}/take", null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Has a <b>second connection</b> adopt the row (Taking → Working with a venue handle) while the first take is
    /// inside the venue call — the concurrent writer the claim's conditional UPDATE must lose to. Written with
    /// <c>ExecuteUpdate</c> so it is a genuine committed write from another context, not a change-tracker edit.
    /// </summary>
    private void AdoptOnFirstTransmit(Guid orderId)
    {
        bool adopted = false;
        VenueFactory.OnPlaceOrder(async () =>
        {
            if (adopted)
            {
                return;
            }

            adopted = true;
            await using AsyncServiceScope concurrent = _factory.Services.CreateAsyncScope();
            TradingCopilotDbContext database = concurrent.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            await database.Orders
                .IgnoreQueryFilters()
                .Where(candidate => candidate.Id == orderId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, OrderStatus.Working)
                    .SetProperty(candidate => candidate.VenueOrderKey, AdoptedVenueKey));
        });
    }

    /// <summary>Drives the firing watcher's core exactly as <see cref="ConditionalOrderHost"/> does after decoding
    /// a quote — real DB, real gate, adversarial venue — with a caller-supplied clock so decisions are deterministic.</summary>
    private async Task<int> FireQuoteAsync(string contractKey, decimal bid, decimal ask, DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ConditionalFiringService firing = scope.ServiceProvider.GetRequiredService<ConditionalFiringService>();
        return await firing.ProcessQuoteAsync(contractKey, bid, ask, now, CancellationToken.None);
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

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must arm cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    /// <summary>Arms a conditional via the create endpoint and returns its id and the resolved contract key.</summary>
    private async Task<(Guid Id, string ContractKey)> ArmConditionalAsync(
        HttpClient client, Guid accountId, string symbol, decimal trigger)
    {
        CreateConditionalOrderRequest request = new(
            Order: ValidProposal(symbol),
            TriggerPrice: trigger,
            TriggerDirection: ConditionalCrossDirection.RisesTo);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's conditional must arm cleanly");
        ConditionalOrderResponse? armed = await response.Content.ReadFromJsonAsync<ConditionalOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(armed);

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

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> ExecuteDbContextQueryAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
