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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for <b>per-record commit in the conditional-firing pass</b> (gh#578, of gh#532 —
/// safety-critical; R-11 / R-16, ADR-0007, ADR-0013), plus the <b>transmit→journal window</b> the durable
/// pre-transmit intent closes (gh#577). Written from the issues and the requirement, not from the implementation
/// (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one behaviour that matters.</b> One quote can resolve <i>several</i> conditionals on the same contract.
/// Each of them is a real entry at a real broker, so the moment one is accepted at the venue its journal — the
/// order row, its stop plan, its gate decision — is the only record that a live order exists. A pass that stages
/// every record's work and commits it once at the end makes every record hostage to every record processed
/// <i>after</i> it: one later venue fault takes the pass down, the post-loop save never runs, and whatever was
/// staged on the way there dies with the disposed context — while the records that had not been reached yet are
/// silently starved with their triggers already crossed. Every case here is therefore built around a fault on a
/// <b>later</b> record and asserts what survives on the <b>earlier</b> ones and what still happens to the
/// <b>later</b> ones.
/// </para>
/// <para>
/// <b>Only an ESCAPING fault can witness that</b>, and choosing the wrong one silently guts the guard. A
/// <see cref="VenueRefusalKind.Definitive"/> rejection is caught by name inside the fire itself (gh#629), which
/// reverts that record and returns a resolved outcome — it never reaches a sibling, however the pass is batched,
/// so a case built on it stays green against the very defect it claims to guard. The fault armed here is
/// therefore the <b>maybe-live</b> class the fire deliberately does <i>not</i> catch — an
/// <see cref="VenueRefusalKind.Indeterminate"/> refusal ("accepted but no order id"), which propagates out of the
/// fire, past the per-account lock, and is contained by the <b>per-record unit of work alone</b>. The definitive
/// arm is not re-proven here: <c>VenueRefusalOutcomeIntegrationTests</c> already owns it (revert to Pending,
/// journal nothing, re-decidable; and its serialization against a concurrent take).
/// </para>
/// <para>
/// <b>Both halves of gh#532 — and what today's code has already made structural.</b> A fired record's journal and
/// its <c>Fired</c> transition now commit <i>inside</i> the account lock (gh#589 / gh#630), so <b>no</b> batched
/// save can discard them; what the per-record unit of work still buys them is <b>containment</b>. The one
/// transition written by the per-record save and nothing else is a <b>cancel / expiry</b> — it never transmits and
/// never takes the lock — so a co-located expiring record (case 3) is the only row a post-loop save could still
/// lose. Discard and starvation turn out to need the same thing: a fault that <b>escapes</b> the record and
/// disposes the owner's context before that save runs. That is exactly the mechanism gh#532 describes ("the
/// escaping exception disposed the owner context before the single save ran"), and exactly why every case here
/// arms a fault the fire does not catch. A batched save that still contained faults per record would leave cases
/// 1–3 green — the suite witnesses the <i>escape</i>, which is the half that does the damage.
/// </para>
/// <para>
/// <b>Why this is a separate class from <see cref="ConditionalFiringIntegrationTests"/>.</b> That suite isolates
/// its tests by giving each one its own instrument — deliberately, because the watcher discovers pending
/// conditionals by contract key. This suite needs the exact opposite shape: <b>several conditionals on ONE
/// contract crossing on ONE quote</b>, which that isolation strategy structurally excludes. It also drives global
/// singleton state on the shared venue double (a placement-counting <c>OnPlaceOrder</c> effect that arms a fault
/// mid-pass, and a poisoning second-connection write), which would leak into every later test in a class it shared.
/// Its own <c>IClassFixture&lt;StubbedVenuePostgresFactory&gt;</c> buys a private container, a private host and a
/// private venue singleton, at the cost of one more container — the isolation is then by construction rather than
/// by convention.
/// </para>
/// <para>
/// <b>Co-location shape.</b> The co-located conditionals sit on <b>one contract key</b> but on <b>separate
/// accounts</b> (separate firms): the fire path's no-stacking check (gh#589) refuses a second entry on an account
/// that already holds an outstanding one, so two conditionals on a single account could never both fire and the
/// case would prove nothing. Each case also owns a contract key no other case uses, so one case's leftover row can
/// never be swept into another's pass.
/// </para>
/// <para>
/// <b>The double is adversarial</b> (the sole sanctioned test double, QA contract §1). It feeds the fault the case
/// names and, when it throws, mints <b>no</b> venue order id — so "the venue certainly accepted this one and not
/// that one" is a property of the seam, read back from <c>PlacedVenueOrderIds</c>, never an answer the stub hands
/// the system. Which record survives, which is left <b>mid-fire</b>, which is starved, and how the fault is
/// classified are all read out of PostgreSQL and out of the service's own log.
/// </para>
/// </remarks>
public class ConditionalFiringPerRecordIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed clock for the deterministic direct-drive fires — firing does not read a wall clock.</summary>
    private static DateTimeOffset Now { get; } = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public ConditionalFiringPerRecordIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // 1. Two conditionals, one contract, one quote — a later record's escaping venue fault must not take the
    //    earlier fire down with it.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldKeepTheEarlierFiredOrderJournaled_WhenALaterRecordOnTheSameQuoteFaultsMaybeLive()
    {
        // FAILURE MODE GUARDED (gh#532): a pass that stages every record's work and commits it once, after the loop —
        // and, with it, a fault on one record that ESCAPES and takes the whole pass down.
        //
        // Two conditionals rest on ONE contract and cross on ONE quote. The FIRST transmit is accepted by the venue —
        // there is now a live order at the broker. The SECOND meets an INDETERMINATE venue refusal ("accepted but no
        // order id"), the maybe-live class the fire deliberately does NOT catch: it propagates out of FireAsync, out
        // of the account lock, and is contained by the per-record unit of work alone. That is precisely why it is the
        // fault armed here. A DEFINITIVE rejection would prove nothing at this seam — FireAsync catches that kind by
        // name (gh#629), reverts that record inside the lock and returns a resolved outcome, so it can never reach a
        // sibling however the pass is batched. (That arm is guarded, single-record, by VenueRefusalOutcomeIntegration-
        // Tests.Fire_ShouldRevertToPending_AndJournalNothing_WhenTheVenueDefinitivelyRefuses.)
        //
        // Collapse the per-record unit of work back into one per-owner context with one post-loop SaveChanges and the
        // escaping fault takes the pass with it: ProcessQuoteAsync never returns a count at all, the post-loop save
        // never runs, and everything staged on the way there dies with the disposed context.
        //
        // The fault is armed on the SECOND transmit of the pass, whichever record that turns out to be (record order
        // within a pass is unspecified), so the failing record is deterministically a LATER one — which is the whole
        // point. Which conditional that was is read back from the correlation handle the fire stamps (gh#577).
        HttpClient client = await AuthenticatedClientAsync();
        Guid firstAccount = await SetupTradeableAccountAsync(client, "Topstep-PerRecordPairA");
        Guid secondAccount = await SetupTradeableAccountAsync(client, "Topstep-PerRecordPairB");
        await DeclareRiskProfileAsync(client, firstAccount);
        await DeclareRiskProfileAsync(client, secondAccount);

        // One contract, two accounts: the co-location the batched-save defect needs, and the only shape in which
        // both records can reach the venue at all (the no-stacking check would refuse a same-account peer).
        (Guid _, string contractKey) = await ArmConditionalAsync(client, firstAccount, "MPA", trigger: 5_010m);
        (Guid _, string peerKey) = await ArmConditionalAsync(client, secondAccount, "MPA", trigger: 5_010m);
        peerKey.Should().Be(contractKey, "both conditionals must rest on ONE contract — that is the shape under test");

        VenueFactory.ClearPlaceOrderFaults();
        int acceptedBefore = VenueFactory.PlacedVenueOrderIds.Count;
        int transmitsBefore = VenueFactory.AllPlacedOrderRequests.Count;
        FaultTheTransmitAfter(healthyTransmits: 1);

        int acted = await FireQuoteThenDisarmAsync(contractKey, bid: 5_009m, ask: 5_010m);

        IReadOnlyList<OrderRequest> transmits = [.. VenueFactory.AllPlacedOrderRequests.Skip(transmitsBefore)];
        transmits.Should().HaveCount(
            2, "both crossed conditionals must be attempted — one record's fault may not starve the other");
        Guid firedId = CorrelatedConditional(transmits[0]);
        Guid strandedId = CorrelatedConditional(transmits[1]);
        strandedId.Should().NotBe(firedId, "the two transmits are two different records");

        acted.Should().Be(
            1,
            "exactly one conditional resolved — a maybe-live fault resolves nothing, and the pass must still RETURN "
            + "that count rather than propagate the fault out of ProcessQuoteAsync");
        (VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore).Should().Be(
            1, "the venue accepted the first entry and minted no id for the faulted one");

        await ExecuteDbContextAsync(async database =>
        {
            ConditionalOrderRecord fired = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == firedId);
            fired.Status.Should().Be(
                ConditionalStatus.Fired,
                "the record whose entry the venue accepted committed BEFORE its peer was touched — a peer's escaping "
                + "fault can neither roll it back nor stop the pass from finishing");
            fired.FiredOrderId.Should().NotBeNull("a fired conditional links the real order it became");

            Order journaled = await database.Orders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == fired.FiredOrderId!.Value);
            journaled.Status.Should().Be(OrderStatus.Working, "the accepted entry rests as a working native order");
            journaled.VenueOrderKey.Should().NotBeNullOrWhiteSpace(
                "the venue handle is the only link back to the live order — losing it strands it");
            journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional, "the order entered via the fire path");
            journaled.AccountId.Should().Be(
                fired.AccountId, "the journal belongs to the fired conditional's own account");

            (await database.StopPlans.IgnoreQueryFilters().CountAsync(plan => plan.OrderId == journaled.Id))
                .Should().Be(
                    1,
                    "the stop plan commits with the order it protects — a rolled-back plan leaves a live entry the "
                    + "stop-promotion watcher cannot see");
            (await database.GateDecisions.IgnoreQueryFilters().CountAsync(decision => decision.OrderId == journaled.Id))
                .Should().Be(1, "the fire-time gate decision is audited alongside the order it approved");

            ConditionalOrderRecord stranded = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == strandedId);
            stranded.Status.Should().Be(
                ConditionalStatus.Firing,
                "the fault is MAYBE-LIVE: the durable pre-transmit intent (gh#577) committed before the venue was "
                + "touched and is deliberately KEPT, because an order may be resting at the broker. Reverting it to "
                + "Pending — the right answer only for a definitive rejection — would let the next quote transmit a "
                + "second entry on top of a live one (gh#589)");
            stranded.FiredOrderId.Should().BeNull("nothing was journaled, so no order can be linked");

            (await database.Orders.IgnoreQueryFilters()
                .AnyAsync(candidate => candidate.AccountId == stranded.AccountId))
                .Should().BeFalse("the faulted record journaled nothing on its own account — that is what strands it");
        });

        // The identity #532 exists to keep: nothing is journaled that the venue did not accept, and every order the
        // venue is KNOWN to have accepted is journaled. The stranded record contributes to neither side — the double
        // threw without minting an id, which is exactly what makes its fate indeterminate. Counted across BOTH
        // co-located accounts, so neither side can hide a drift.
        int journaledOnBothAccounts = await ExecuteDbContextQueryAsync(database => database.Orders
            .IgnoreQueryFilters()
            .CountAsync(candidate => candidate.AccountId == firstAccount || candidate.AccountId == secondAccount));
        journaledOnBothAccounts.Should().Be(
            VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore,
            "the count of orders the venue certainly accepted must equal the count journaled — the whole safety "
            + "claim of committing per record");
    }

    // =============================================================================================================
    // 2. Containment — three conditionals, the middle transmit faults; one poison record must not starve its peers.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldFireBothHealthyRecords_WhenTheMiddleTransmitOnTheSameQuoteFaultsMaybeLive()
    {
        // FAILURE MODE GUARDED (gh#532, the containment half): a fault that aborts the pass. Three conditionals rest
        // on ONE contract and cross on ONE quote; only the MIDDLE transmit meets the INDETERMINATE (maybe-live) venue
        // refusal the fire does not catch, so it escapes FireAsync and the account lock and can only be stopped by
        // the per-record unit of work. A pass that lets it escape the loop never reaches the third record at all — it
        // is silently starved, its trigger crossed and nothing done about it. Both healthy records must be fired and
        // journaled, and only the faulted one left mid-fire.
        //
        // A definitive rejection could not express this case: FireAsync catches that kind itself and returns
        // normally, so the loop always continues and the third record is never at risk. The whole starvation property
        // hangs on the fault being one that ESCAPES.
        //
        // The fault is armed on the second transmit of the pass and cleared as it fires, so the failure is exactly in
        // the middle whatever order the records are processed in.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountOne = await SetupTradeableAccountAsync(client, "Topstep-PerRecordTrioA");
        Guid accountTwo = await SetupTradeableAccountAsync(client, "Topstep-PerRecordTrioB");
        Guid accountThree = await SetupTradeableAccountAsync(client, "Topstep-PerRecordTrioC");
        await DeclareRiskProfileAsync(client, accountOne);
        await DeclareRiskProfileAsync(client, accountTwo);
        await DeclareRiskProfileAsync(client, accountThree);

        (Guid _, string contractKey) = await ArmConditionalAsync(client, accountOne, "MPB", trigger: 5_010m);
        await ArmConditionalAsync(client, accountTwo, "MPB", trigger: 5_010m);
        await ArmConditionalAsync(client, accountThree, "MPB", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        int acceptedBefore = VenueFactory.PlacedVenueOrderIds.Count;
        int transmitsBefore = VenueFactory.AllPlacedOrderRequests.Count;
        FaultTheTransmitAfter(healthyTransmits: 1);

        int acted = await FireQuoteThenDisarmAsync(contractKey, bid: 5_009m, ask: 5_010m);

        IReadOnlyList<OrderRequest> transmits = [.. VenueFactory.AllPlacedOrderRequests.Skip(transmitsBefore)];
        transmits.Should().HaveCount(
            3,
            "all three crossed conditionals must reach the transmit — a record that faults in the middle of the pass "
            + "must not stop the pass, or the last record is starved with its trigger already crossed");
        Guid firstFired = CorrelatedConditional(transmits[0]);
        Guid strandedId = CorrelatedConditional(transmits[1]);
        Guid lastFired = CorrelatedConditional(transmits[2]);

        acted.Should().Be(2, "the two records whose entries the venue accepted resolved; the faulted one did not");
        (VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore).Should().Be(
            2, "the venue accepted two of the three entries and minted no id for the faulted one");

        await ExecuteDbContextAsync(async database =>
        {
            foreach (Guid survivorId in new[] { firstFired, lastFired })
            {
                ConditionalOrderRecord survivor = await database.ConditionalOrders
                    .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == survivorId);
                survivor.Status.Should().Be(
                    ConditionalStatus.Fired,
                    "a record whose own transmit the venue accepted is committed on its own save — neither the peer "
                    + "that failed before it nor the one after it can undo that");
                survivor.FiredOrderId.Should().NotBeNull();

                Order journaled = await database.Orders
                    .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == survivor.FiredOrderId!.Value);
                journaled.Status.Should().Be(OrderStatus.Working);
                journaled.VenueOrderKey.Should().NotBeNullOrWhiteSpace();
                journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional);
                (await database.StopPlans.IgnoreQueryFilters().CountAsync(plan => plan.OrderId == journaled.Id))
                    .Should().Be(1, "each surviving entry keeps the stop plan that protects it");
                (await database.GateDecisions.IgnoreQueryFilters()
                    .CountAsync(decision => decision.OrderId == journaled.Id))
                    .Should().Be(1, "each surviving entry keeps its audited fire-time decision");
            }

            ConditionalOrderRecord stranded = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == strandedId);
            stranded.Status.Should().Be(
                ConditionalStatus.Firing,
                "only the faulted record is left mid-fire — its own order may be resting at the venue, so its durable "
                + "intent is kept rather than re-decided (gh#577 / gh#589); its peers are unaffected either way");
            stranded.FiredOrderId.Should().BeNull();
            (await database.Orders.IgnoreQueryFilters()
                .AnyAsync(candidate => candidate.AccountId == stranded.AccountId))
                .Should().BeFalse("the faulted record journaled nothing");
        });

        int journaledOnAllThree = await ExecuteDbContextQueryAsync(database => database.Orders
            .IgnoreQueryFilters()
            .CountAsync(candidate => candidate.AccountId == accountOne
                || candidate.AccountId == accountTwo
                || candidate.AccountId == accountThree));
        journaledOnAllThree.Should().Be(
            VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore,
            "accepted-at-the-venue and journaled must still match exactly when a record fails between them");
    }

    // =============================================================================================================
    // 3. The DISCARD half — an earlier record's expiry is committed on its own save, not staged for a post-loop one.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldCommitAnEarlierRecordsExpiry_WhenALaterRecordOnTheSameQuoteFaultsMaybeLive()
    {
        // FAILURE MODE GUARDED (gh#532, the DISCARD half — the one cases 1 and 2 can no longer witness). A fired
        // record's journal and its Fired transition commit INSIDE the account lock (gh#589 / gh#630), so they survive
        // a batched post-loop save by construction. A cancel / expiry does not: it never transmits, never takes the
        // lock, and its transition is written by the per-record SaveChanges and by nothing else. It is therefore the
        // only work in the pass that a single post-loop save can still silently lose.
        //
        // Three conditionals rest on ONE contract and cross on ONE quote: one whose validity window has already
        // passed at fire time (it EXPIRES — expiry outranks firing, so it never transmits), and two that transmit,
        // the second of which meets the escaping maybe-live refusal. Collapse the per-record unit of work into one
        // per-owner context with one post-loop save and the expiry is lost whichever order the records come in — it
        // was staged into a context the escaping fault disposes before the post-loop save runs, or the record was
        // never reached at all. Either way the row still reads Pending, and a stale setup the system believes it
        // abandoned is live again on the next quote.
        HttpClient client = await AuthenticatedClientAsync();
        Guid expiringAccount = await SetupTradeableAccountAsync(client, "Topstep-PerRecordDiscardA");
        Guid firingAccount = await SetupTradeableAccountAsync(client, "Topstep-PerRecordDiscardB");
        Guid faultingAccount = await SetupTradeableAccountAsync(client, "Topstep-PerRecordDiscardC");
        await DeclareRiskProfileAsync(client, expiringAccount);
        await DeclareRiskProfileAsync(client, firingAccount);
        await DeclareRiskProfileAsync(client, faultingAccount);

        // Valid at arm time, past its window at fire time — the same shape the single-record expiry case uses, but
        // co-located with records that DO transmit so a peer's fault can reach it.
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        DateTimeOffset firedAt = expiry.AddMinutes(1);
        (Guid expiringId, string contractKey) =
            await ArmConditionalAsync(client, expiringAccount, "MPD", trigger: 5_010m, expiresAt: expiry);
        await ArmConditionalAsync(client, firingAccount, "MPD", trigger: 5_010m);
        await ArmConditionalAsync(client, faultingAccount, "MPD", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        int acceptedBefore = VenueFactory.PlacedVenueOrderIds.Count;
        int transmitsBefore = VenueFactory.AllPlacedOrderRequests.Count;
        FaultTheTransmitAfter(healthyTransmits: 1);

        int acted = await FireQuoteThenDisarmAsync(contractKey, bid: 5_009m, ask: 5_010m, now: firedAt);

        // Only two of the three ever reach the venue — the expiring one is resolved before the transmit is even
        // considered — so the fault still lands on the LATER of the two that do.
        IReadOnlyList<OrderRequest> transmits = [.. VenueFactory.AllPlacedOrderRequests.Skip(transmitsBefore)];
        transmits.Should().HaveCount(2, "the expiring record never transmits; the other two both must");
        Guid firedId = CorrelatedConditional(transmits[0]);
        Guid strandedId = CorrelatedConditional(transmits[1]);

        acted.Should().Be(2, "the expiry and the accepted fire each resolved; the faulted record resolved nothing");
        (VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore).Should().Be(
            1, "the venue accepted one entry and minted no id for the faulted one");

        await ExecuteDbContextAsync(async database =>
        {
            ConditionalOrderRecord expired = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == expiringId);
            expired.Status.Should().Be(
                ConditionalStatus.Expired,
                "THE assertion: the expiry is committed on the expiring record's OWN save, before any sibling is "
                + "touched. It transmits nothing and takes no account lock, so a single post-loop save is the only "
                + "thing that could have written it — and a later record's escaping fault would take that save with "
                + "it, leaving an abandoned setup readable as Pending and live again on the next quote");
            expired.FiredOrderId.Should().BeNull("an expired conditional never became an order");
            (await database.Orders.IgnoreQueryFilters()
                .AnyAsync(candidate => candidate.AccountId == expiringAccount))
                .Should().BeFalse("an expired conditional journals nothing");

            ConditionalOrderRecord fired = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == firedId);
            fired.Status.Should().Be(ConditionalStatus.Fired, "the accepted entry still resolved to Fired");
            fired.FiredOrderId.Should().NotBeNull();
            (await database.StopPlans.IgnoreQueryFilters()
                .CountAsync(plan => plan.OrderId == fired.FiredOrderId!.Value))
                .Should().Be(1, "the fired entry keeps the stop plan that protects it");

            ConditionalOrderRecord stranded = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == strandedId);
            stranded.Status.Should().Be(
                ConditionalStatus.Firing, "the maybe-live record keeps its durable intent — it may be live at the venue");
        });
    }

    // =============================================================================================================
    // 4. The transmit→journal window (gh#577) — the venue ACCEPTS, then the journal commit fails.
    // =============================================================================================================

    [Fact]
    public async Task Fire_ShouldStayFiring_AndNeverReFire_WhenTheVenueAcceptsButTheJournalCommitFails()
    {
        // FAILURE MODE GUARDED (gh#577, the dangerous window ADR-0013 names): an order is LIVE at the venue and the
        // system has no journal of it. This is the one window the unit tier cannot reach — the in-process journaling
        // helpers do not throw, so the only reachable post-accept fault is a real SaveChangesAsync failure against
        // real PostgreSQL. It is injected here by having a SECOND connection, inside the venue's own PlaceOrderAsync,
        // claim the venue order key the stub has just minted; the fire's journal INSERT then collides with the unique
        // (AccountId, VenueOrderKey) index and its whole unit of work — the order row, the stop plan, the gate
        // decision, the Fired transition — rolls back.
        //
        // Three things must hold, and the third is the only proof gh#577 bought anything:
        //   (a) the order really is live at the venue (the double minted an id for it);
        //   (b) the conditional is left FIRING — the durable pre-transmit intent, not Pending and not Fired — and the
        //       fault is surfaced as the DANGEROUS, indeterminate case at Error, not as the benign "re-decides next
        //       quote" Warning that a fault BEFORE the transmit earns;
        //   (c) the very next crossing quote does NOT re-fire it. Discovery is Pending-only, so a conditional left
        //       Pending here would be picked up again and a SECOND live order transmitted on top of the first — the
        //       double-transmit that this whole design exists to prevent. Nothing else in the conditional-firing
        //       suites witnesses (c) for a journal fault.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-PerRecordJournalFault");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(client, accountId, "MPC", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        int acceptedBefore = VenueFactory.PlacedVenueOrderIds.Count;
        int stopPlansBefore = await ExecuteDbContextQueryAsync(database =>
            database.StopPlans.IgnoreQueryFilters().CountAsync());
        Guid claimantOrderId = await ClaimTheVenueKeyOnFirstTransmitAsync(accountId);

        // Driven through a hand-built service so the classification can be read off the service's OWN log; every
        // other dependency is the real one resolved from the host container.
        (int acted, CapturingLogger log) =
            await FireQuoteCapturingLogsThenDisarmAsync(contractKey, bid: 5_009m, ask: 5_010m);

        // (a) The venue took the order. This is what makes the window dangerous rather than merely untidy.
        (VenueFactory.PlacedVenueOrderIds.Count - acceptedBefore).Should().Be(
            1, "the venue ACCEPTED the entry — an order is resting at the broker regardless of what the journal did");
        acted.Should().Be(0, "the fire did not complete, so nothing was resolved this quote");

        // (b) The durable intent survives the fault, and the fault is classified as the dangerous one.
        await ExecuteDbContextAsync(async database =>
        {
            ConditionalOrderRecord record = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Firing,
                "the pre-transmit intent committed BEFORE the venue was touched, so a journal fault leaves the "
                + "conditional mid-fire — not Pending (which discovery would re-fire) and not Fired (which would "
                + "claim a journal that does not exist)");
            record.FiredOrderId.Should().BeNull("no order was journaled, so none can be linked");

            // Nothing execution-shaped committed: the journal, its stop plan and its decision are one unit of work.
            List<Order> orders = await database.Orders
                .IgnoreQueryFilters().Where(candidate => candidate.AccountId == accountId).ToListAsync();
            orders.Should().ContainSingle("only the claimant row the fault injector wrote is on this account");
            orders[0].Id.Should().Be(claimantOrderId, "the fire's own order row rolled back with its failed commit");
            (await database.StopPlans.IgnoreQueryFilters().CountAsync()).Should().Be(
                stopPlansBefore,
                "the stop plan was staged in the same failed commit — a plan that survived would point at an order "
                + "row that does not exist");
            (await database.GateDecisions.IgnoreQueryFilters()
                .AnyAsync(decision => decision.AccountId == accountId))
                .Should().BeFalse("the gate decision was staged in the same failed commit, so it rolled back too");
        });

        log.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("may be live at the venue and unrecorded", StringComparison.Ordinal),
            "an order the venue accepted but the system did not record is the indeterminate, may-be-live case — it "
            + "must be surfaced at Error for a human or the reconcile, never swallowed");
        log.Entries.Should().NotContain(
            entry => entry.Message.Contains("re-decides on the next quote", StringComparison.Ordinal),
            "classifying this as the benign 'nothing was transmitted, try again' fault would tell the operator the "
            + "opposite of the truth — something IS resting at the broker");

        // (c) THE assertion. The venue is healthy again and the trigger still crosses: a conditional left Pending
        // would be rediscovered and a second live order transmitted on top of the first. Driven through the
        // DI-registered service, i.e. exactly the wiring the background host runs.
        int transmitsBefore = VenueFactory.AllPlacedOrderRequests.Count;
        int acceptedAfterFault = VenueFactory.PlacedVenueOrderIds.Count;

        int reacted = await FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m);

        reacted.Should().Be(0, "a mid-fire conditional is never rediscovered — discovery is Pending-only");
        VenueFactory.AllPlacedOrderRequests.Count.Should().Be(
            transmitsBefore,
            "the replayed quote must not even ATTEMPT a transmit: the previous fire's order may be resting live at "
            + "the venue, and a second entry on top of it is the exact failure gh#577 exists to prevent");
        VenueFactory.PlacedVenueOrderIds.Count.Should().Be(
            acceptedAfterFault, "and so no second order can have been accepted");

        await ExecuteDbContextAsync(async database =>
        {
            ConditionalOrderRecord record = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Firing, "it stays stranded mid-fire until a reconcile resolves it against venue truth");
            (await database.Orders.IgnoreQueryFilters()
                .CountAsync(candidate => candidate.AccountId == accountId && candidate.Status == OrderStatus.Working))
                .Should().Be(0, "the replay journaled no second working entry");
        });
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>
    /// Lets the first <paramref name="healthyTransmits"/> transmits of the pass through and faults the <b>next</b>
    /// one with an <b>escaping, maybe-live</b> venue refusal, then restores the venue for the rest of the pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this kind and not a definitive rejection.</b> Only a fault that ESCAPES the record can reach a sibling,
    /// and so only an escaping fault can witness what the per-record unit of work is for. The fire catches
    /// <see cref="VenueRefusalKind.Definitive"/> by name inside <c>FireAsync</c> (gh#629), reverts that record and
    /// returns a resolved outcome — a fault that cannot escape cannot discard or starve anything, so a case built on
    /// it would stay green against the batched-save defect it claims to guard.
    /// <see cref="VenueRefusalKind.Indeterminate"/> — the gateway accepted but returned no order id, so the order may
    /// be LIVE — is deliberately not caught there and propagates out of the fire and out of the account lock, which
    /// is exactly the fault the pass must contain per record.
    /// </para>
    /// <para>
    /// Arming is done from inside the double's own place-order side effect, so the fault lands on a transmit
    /// <i>ordinal</i> rather than on a particular record — record order within a pass is unspecified, and the
    /// property under test is precisely that a fault on a <b>later</b> record cannot undo or starve an
    /// <b>earlier</b> one. Which record faulted is read back afterwards from the correlation handle the fire stamped
    /// on each transmit.
    /// </para>
    /// </remarks>
    private void FaultTheTransmitAfter(int healthyTransmits)
    {
        int transmits = 0;
        VenueFactory.OnPlaceOrder(() =>
        {
            transmits++;
            if (transmits == healthyTransmits)
            {
                // The double only FEEDS the fault, and mints no venue order id when it throws. What the fire does
                // with a maybe-live refusal — keep the durable intent, revert it, journal something — and whether the
                // pass survives it at all are production's decisions alone.
                VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
                    "ProjectX accepted the order but returned no order id.", VenueRefusalKind.Indeterminate));
            }
            else if (transmits == healthyTransmits + 1)
            {
                VenueFactory.ClearPlaceOrderFaults();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Poisons the <b>journal commit</b> of the first transmit of the pass: from a second connection, inside the
    /// venue's <c>PlaceOrderAsync</c> and after it has minted the venue order id, insert an order row claiming that
    /// very <c>(AccountId, VenueOrderKey)</c>. The fire's own journal INSERT then violates the unique index and its
    /// transaction rolls back — a genuine <c>SaveChangesAsync</c> failure after the venue accepted, which is the only
    /// way to reach the transmit→journal window and is unreachable on any in-memory provider.
    /// </summary>
    /// <returns>The id of the claimant row, so the assertions can tell it from a surviving journal row.</returns>
    private async Task<Guid> ClaimTheVenueKeyOnFirstTransmitAsync(Guid accountId)
    {
        Guid ownerId = await ExecuteDbContextQueryAsync(database => database.Accounts
            .IgnoreQueryFilters().Where(account => account.Id == accountId).Select(account => account.UserId)
            .SingleAsync());
        Guid claimantId = Guid.NewGuid();
        bool claimed = false;

        VenueFactory.OnPlaceOrder(async () =>
        {
            if (claimed)
            {
                return;
            }

            claimed = true;
            await using AsyncServiceScope concurrent = _factory.Services.CreateAsyncScope();
            TradingCopilotDbContext database = concurrent.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            database.Orders.Add(new Order
            {
                Id = claimantId,
                UserId = ownerId,
                AccountId = accountId,
                Instrument = "CLAIMANT",
                Symbol = "MPC",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                // Terminal on purpose: the no-stacking allow-list ignores Cancelled, so this row poisons the journal
                // commit without also blocking the replay the last assertion depends on.
                Status = OrderStatus.Cancelled,
                Mode = TradingMode.Practice,
                EntryMethod = OrderEntryMethod.Manual,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                VenueOrderKey = VenueFactory.PlacedVenueOrderIds[^1],
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync();
        });

        return claimantId;
    }

    /// <summary>Clears every armed fault and side effect — they live on the class-wide singleton double.</summary>
    private void DisarmTheVenue()
    {
        VenueFactory.OnPlaceOrder(null);
        VenueFactory.ClearPlaceOrderFaults();
    }

    /// <summary>The conditional a transmit belongs to, from the correlation handle the fire stamps (gh#577).</summary>
    private static Guid CorrelatedConditional(OrderRequest request)
    {
        request.CustomTag.Should().NotBeNullOrWhiteSpace(
            "a fired conditional stamps its own id on the venue order, so a stranded fire can be matched back to it");
        return Guid.Parse(request.CustomTag!);
    }

    /// <summary>Drives the firing watcher's core exactly as <see cref="ConditionalOrderHost"/> does after decoding
    /// a quote, through the DI-registered service — real DB, real gate, adversarial venue, fixed clock.</summary>
    private async Task<int> FireQuoteAsync(string contractKey, decimal bid, decimal ask, DateTimeOffset? now = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ConditionalFiringService firing = scope.ServiceProvider.GetRequiredService<ConditionalFiringService>();
        return await firing.ProcessQuoteAsync(contractKey, bid, ask, now ?? Now, CancellationToken.None);
    }

    /// <summary>
    /// The same pass, disarming the venue in a <c>finally</c>. The double is a class-wide singleton, so a pass that
    /// does <b>not</b> return normally — the very regression these cases guard — would otherwise leave the armed
    /// fault and its transmit counter in place and fail every later case for the wrong reason. Keeping the teardown
    /// unconditional means each case's red is its own.
    /// </summary>
    private async Task<int> FireQuoteThenDisarmAsync(
        string contractKey, decimal bid, decimal ask, DateTimeOffset? now = null)
    {
        try
        {
            return await FireQuoteAsync(contractKey, bid, ask, now);
        }
        finally
        {
            DisarmTheVenue();
        }
    }

    /// <summary>
    /// The same pass, but with the service constructed by hand so its <see cref="ILogger"/> can be captured — the
    /// Error-vs-Warning classification of a maybe-live fault is a required behaviour and there is no other seam to
    /// read it from. Every other dependency is resolved from the real host container, so nothing but the log sink
    /// differs from <see cref="FireQuoteAsync"/>.
    /// </summary>
    private async Task<(int Acted, CapturingLogger Log)> FireQuoteCapturingLogsAsync(
        string contractKey, decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CapturingLogger log = new();
        ConditionalFiringService firing = new(
            scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>(),
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>(),
            scope.ServiceProvider.GetRequiredService<IOptions<ProjectXConnectionOptions>>(),
            scope.ServiceProvider.GetRequiredService<IOptions<ExecutionOptions>>(),
            scope.ServiceProvider.GetRequiredService<HostTradingEnvironment>(),
            scope.ServiceProvider.GetRequiredService<IKillSwitch>(),
            scope.ServiceProvider.GetRequiredService<IAccountEntryGuard>(),
            log);
        int acted = await firing.ProcessQuoteAsync(contractKey, bid, ask, Now, CancellationToken.None);
        return (acted, log);
    }

    /// <summary>
    /// <see cref="FireQuoteCapturingLogsAsync"/> with the unconditional venue teardown
    /// <see cref="FireQuoteThenDisarmAsync"/> uses, for the same reason: the poisoning side effect lives on a
    /// class-wide singleton, so a pass that throws must not leave it armed for the next case.
    /// </summary>
    private async Task<(int Acted, CapturingLogger Log)> FireQuoteCapturingLogsThenDisarmAsync(
        string contractKey, decimal bid, decimal ask)
    {
        try
        {
            return await FireQuoteCapturingLogsAsync(contractKey, bid, ask);
        }
        finally
        {
            DisarmTheVenue();
        }
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

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
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
            MaxDrawdownPerTrade: 300m,
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
    /// <param name="client">The authenticated operator client.</param>
    /// <param name="accountId">The account the conditional rests on.</param>
    /// <param name="symbol">The instrument — one per case, so cases never share a contract key.</param>
    /// <param name="trigger">The trigger price.</param>
    /// <param name="expiresAt">
    /// The optional validity deadline. Set only by the discard case, whose co-located record must resolve by
    /// <b>expiry</b> rather than by a transmit — the one transition the per-record save alone commits.
    /// </param>
    private async Task<(Guid Id, string ContractKey)> ArmConditionalAsync(
        HttpClient client, Guid accountId, string symbol, decimal trigger, DateTimeOffset? expiresAt = null)
    {
        CreateConditionalOrderRequest request = new(
            Order: ValidProposal(symbol),
            TriggerPrice: trigger,
            TriggerDirection: ConditionalCrossDirection.RisesTo,
            ExpiresAt: expiresAt);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's conditional must arm cleanly");
        ConditionalOrderResponse? armed = await response.Content.ReadFromJsonAsync<ConditionalOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(armed);

        string contractKey = await ExecuteDbContextQueryAsync(database => database.ConditionalOrders
            .IgnoreQueryFilters().AsNoTracking()
            .Where(candidate => candidate.Id == armed.ConditionalOrderId)
            .Select(candidate => candidate.Instrument)
            .SingleAsync());

        return (armed.ConditionalOrderId, contractKey);
    }

    private static SendOrderRequest ValidProposal(string symbol) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: 1,
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

    /// <summary>A minimal <see cref="ILogger{T}"/> recording what the firing pass logged, so the Error-vs-Warning
    /// classification of a maybe-live fault can be asserted directly (the service is constructed with it).</summary>
    private sealed class CapturingLogger : ILogger<ConditionalFiringService>
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
