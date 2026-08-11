using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the <b>adopt-still-open</b> arm of <c>POST /orders/{id}/reconcile</c> — gh#769, the
/// container-tier proof of gh#723 (of gh#619, part 3; R-8 / R-9 / R-11 / R-16 / R-20, ADR-0007, ADR-0013). Written
/// from the issue and ADR-0013, <b>not</b> from the implementation (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>The strand this arm clears.</b> A take that faulted at the venue seam <i>after</i> the venue had already
/// filled it is the likeliest strand in practice, and it is invisible to both of the reconcile's other reads: a
/// filled entry is no longer a <i>working order</i>, and its bracket legs carry no <c>customTag</c>, so the resting
/// read finds nothing; the account is <i>not flat</i>, so the release arm must never run. Only <b>fill history</b>
/// for the row's own tag separates "this take executed and its position is still open" from "some other exposure is
/// sitting on this account". On a positively reported fill the row is adopted <see cref="OrderStatus.Filled"/>;
/// every other answer keeps the pre-gh#723 refusal.
/// </para>
/// <para>
/// <b>Why the container tier, and why this suite could not exist before gh#769.</b> The decision is made from real
/// <c>ReconcileAsync</c> round trips and committed through the applied migrations, and the strand is produced the
/// way production produces it — a genuine maybe-live fault at the seam, never a hand-written <c>Taking</c> row. The
/// missing piece was the venue double: it could express a <i>resting</i> order and an <i>open position</i> but not
/// an <i>execution</i>, so no test here could reach a positive <see cref="TaggedFillStatus.Filled"/> and the adopt
/// was provable nowhere below staging. <see cref="AdversarialTestProjectXVenueFactory.SeedTaggedFill"/> is that
/// seam, added with this suite.
/// </para>
/// <para>
/// <b>The three cases fail in different directions, which is why all three are here.</b> Failing to adopt a
/// confirmed fill leaves the operator with a permanently blocked account and a trade missing from the journal
/// (R-8/R-9). Adopting on anything <i>weaker</i> than a positive fill stamps a venue key — and a realised trade —
/// onto a position that may belong to something else entirely. A suite carrying only the adopt would pass against
/// an implementation that adopted on every answer, which is the more dangerous of the two mistakes.
/// </para>
/// <para>
/// <b>The red these guard.</b> On the base this suite branched from, <c>POST /orders/{id}/reconcile</c> answers an
/// open position with a flat refusal and never consults fill history at all — so the adopt case is red there for
/// exactly the defect it names, and every <i>gh#723</i> case reaches green only once #768 lands (the gh#631 case
/// below is deliberately the exception — it exercises behaviour that already ships). The two veto cases would
/// otherwise agree with that blanket refusal for the wrong reason, which is a test that cannot fail on its subject:
/// each therefore reconciles the <b>same fixture twice</b>, once with the venue's answer withheld and once with it
/// supplied, so the refusal is shown to turn on the answer rather than on the account merely being non-flat. The
/// gh#631 case at the end is the seam's own positive control — it exercises
/// <see cref="AdversarialTestProjectXVenueFactory.SeedTaggedFill"/> against behaviour that already ships, so a stub
/// that never reached <c>FindFilledOrderByTagAsync</c> cannot masquerade as a missing feature.
/// </para>
/// <para>
/// <b>The double stays adversarial.</b> It feeds the venue's report — an executed size, a price, the venue's own
/// order handle, or a read that faults — and never the reconcile's decision about it. In particular the
/// <see cref="TaggedFillStatus.Unavailable"/> case is fed as a <b>throwing read</b>, not as the status itself:
/// mapping a failed read to "unavailable, which is NOT 'did not fill'" is production's rule, and a stub that handed
/// that status back would be supplying the very answer under test. Hosts are suppressed
/// (<see cref="OcoExitTestPostgresFactory"/>, the sanctioned reuse other suites make of it purely for host
/// suppression) so no background flatten / orphan-guard pass acts on the seeded open position.
/// </para>
/// </remarks>
public class ReconcileAdoptStillOpenIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    /// <summary>The venue's own handle on the executed order — the one thing a position snapshot cannot carry.</summary>
    private const string FilledVenueKey = "FILLED-AT-VENUE-769";

    /// <summary>The contract the still-open position sits on; the adversarial venue resolves MES to this.</summary>
    private const string OpenContractKey = "MESM25";

    /// <summary>What the operator armed. Deliberately larger than <see cref="FilledSize"/> — see the adopt case.</summary>
    private const int ArmedSize = 2;

    /// <summary>What the venue actually executed: a <b>partial</b> fill, and a partial fill is a fill.</summary>
    private const decimal FilledSize = 1m;

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    /// <summary>The refusal bodies on this route are anonymous <c>{ error = "…" }</c> objects, never ProblemDetails.</summary>
    private sealed record ErrorResponse(string? Error);

    public ReconcileAdoptStillOpenIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // 1. The adopt: a positively reported fill over a still-open position.
    // =================================================================================================================

    [Fact]
    public async Task Reconcile_ShouldAdoptTheRowAsFilled_WhenFillHistoryConfirmsTheTakeAndThePositionIsStillOpen()
    {
        // gh#723's whole subject. Nothing rests under the tag and the account is NOT flat, so neither of the older
        // arms may run: releasing would strand an untracked live position (gh#589 round-2), and there is no working
        // order to adopt. Venue fill history is the only thing that can say the position is THIS take's, so the
        // assertions below are all about taking venue truth rather than what the app believed it sent.
        Fixture fixture = await StrandATakeAsync("Topstep-769-Adopt");

        VenueFactory.SeedPosition(fixture.VenueAccountKey, OpenContractKey, netQuantity: (int)FilledSize);
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey,
            fixture.OrderId.ToString(),
            FilledSize,
            filledPrice: 5_001.25m,
            venueOrderKey: FilledVenueKey);
        int queriesBefore = VenueFactory.FillHistoryQueries.Count;

        using HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a strand the venue positively reports as FILLED is resolvable — leaving it stuck Taking is the very "
            + "dead-end gh#723 exists to clear " + await DescribeAsync(reconcile));

        // Read from a FRESH scope: what an independent reader sees over Postgres, not the acting scope's tracker.
        Order adopted = await OrderAsync(fixture.OrderId);

        adopted.Status.Should().Be(
            OrderStatus.Filled,
            "the take executed, so the row records the trade that happened — Working would claim an order is resting "
            + "when none is, and Staged would discard a real fill from the journal (R-8/R-9)");
        adopted.VenueOrderKey.Should().Be(
            FilledVenueKey,
            "the key comes from the FILL, which is the only read that carries one — a position snapshot has no handle, "
            + "so an adopt that recorded none would leave the trade unmatchable against the venue");
        adopted.Size.Should().Be(
            (int)FilledSize,
            $"the row is sized to what the venue EXECUTED ({FilledSize}), never to what the operator armed "
            + $"({ArmedSize}); recording the armed size would overstate a partial fill and mis-state realised risk");

        (await StopPlanCountAsync(fixture.OrderId)).Should().Be(
            0,
            "the still-open position rides the NATIVE bracket the venue attached on fill (a position is never opened "
            + "unprotected, gh#589) — a synthetic promotion plan would race a SECOND native stop over that same leg");

        // The disposition: a confirmed fill is a taken suggestion, and the R-9 loop only ever sees what was journaled.
        SuggestionDisposition disposition = await DispositionAsync(fixture.SuggestionId);
        disposition.Kind.Should().Be(
            SuggestionDispositionKind.Taken,
            "the operator took this suggestion unmodified, and the adopt is the point that finally becomes durable — "
            + "an adopt that skipped the journal would silently drop the trade from the R-9 learning loop (gh#549)");
        disposition.Deviations.Should().Be(
            SuggestionDeviation.None, "nothing was edited between the suggestion and the armed ticket");
        disposition.TakenSize.Should().Be(
            ArmedSize,
            "the disposition records what the OPERATOR submitted, not what the venue filled — collapsing the two "
            + "would tell the R-9 loop the operator asked for a smaller trade than they did");

        // The window the fill-history read was given. Production's own rule is that it must start at or before the
        // attempt; one starting later would miss the fill and let a release proceed over a live position. The stub
        // deliberately does not enforce it, so that a window that drifted forward is visible here rather than hidden.
        IReadOnlyList<(string AccountKey, string CustomTag, DateTimeOffset Since)> queries =
            [.. VenueFactory.FillHistoryQueries.Skip(queriesBefore)];
        queries.Should().NotBeEmpty("the adopt decision must be made from fill history, not inferred from the position");
        queries[0].CustomTag.Should().Be(
            fixture.OrderId.ToString(),
            "the read asks about THIS row's tag — asking about anything else would attribute a stranger's fill to it");
        queries[0].Since.Should().BeOnOrBefore(
            fixture.AttemptedAt,
            "the search window must already cover the transmit attempt; a window opening after it would report no "
            + "fill for a take that really filled, and the row would be released over a live position");
    }

    // =================================================================================================================
    // 2. The veto stays a veto: nothing weaker than a positive fill may adopt.
    // =================================================================================================================

    [Fact]
    public async Task Reconcile_ShouldLeaveTheRowTaking_WhenTheFillHistoryReadCannotBeCompleted()
    {
        // "We could not ask" is not "it did not fill", and it is equally not "it did". A fill IS seeded here, so the
        // only thing standing between the reconcile and a correct adopt is the failed read — which makes this a test
        // of the refusal itself rather than of an empty fixture. Adopting on an unknown would stamp a venue key and a
        // realised trade onto a position the system cannot attribute; the safe state is to stay stranded and loud.
        Fixture fixture = await StrandATakeAsync("Topstep-769-Unavailable");

        VenueFactory.SeedPosition(fixture.VenueAccountKey, OpenContractKey, netQuantity: (int)FilledSize);
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey, fixture.OrderId.ToString(), FilledSize, venueOrderKey: FilledVenueKey);
        VenueFactory.MakeFillHistoryUnreadable(fixture.VenueAccountKey);

        using HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "an unreadable fill history resolves nothing — reporting success would tell the operator the strand is "
            + "cleared when it is not " + await DescribeAsync(reconcile));

        await AssertStillStrandedAsync(
            fixture,
            "a read that FAILED is not a read that returned 'no fill' — the row stays Taking so the account stays "
            + "blocked, which is the safe direction (ADR-0013 §9)");

        // The refusal above would look identical if the reconcile refused on the OPEN POSITION alone and never asked
        // fill history at all — the pre-gh#723 behaviour. So show the fixture was one readable answer away from
        // adopting: restore the read, change nothing else, and the same request must now resolve. That is what makes
        // this a test of the veto rather than a test that happens to agree with a blanket refusal.
        VenueFactory.ClearFillHistoryFaults();

        using HttpResponseMessage retry = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        retry.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "with the venue's answer restored the SAME fixture adopts, so the refusal above was caused by the "
            + "unreadable read and not by the open position on its own " + await DescribeAsync(retry));
        (await OrderAsync(fixture.OrderId)).Status.Should().Be(
            OrderStatus.Filled, "the retry resolves the strand the failed read could only postpone");
    }

    [Fact]
    public async Task Reconcile_ShouldLeaveTheRowTaking_WhenTheVenuePositivelyReportsNoFillUnderTheTag()
    {
        // The subtlest of the three. NoFillFound is a genuine report from a reachable venue that does carry history —
        // but it is a NEGATIVE EXISTENCE CLAIM over an external search index, and those authorise nothing. Here it
        // also means the open position is not attributable to this take, so adopting would stamp this row onto
        // somebody else's exposure. Releasing is equally forbidden: the position is live.
        Fixture fixture = await StrandATakeAsync("Topstep-769-NoFill");

        VenueFactory.SeedPosition(fixture.VenueAccountKey, OpenContractKey, netQuantity: (int)FilledSize);
        VenueFactory.MakeFillHistoryEmpty(fixture.VenueAccountKey); // reachable, carries history, reports no execution

        using HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "an open position that fill history does not attribute to this take is not resolvable from here "
            + await DescribeAsync(reconcile));

        await AssertStillStrandedAsync(
            fixture,
            "only a positively reported fill may adopt; a negative existence claim over the venue's index must never "
            + "be promoted into authority to write a trade onto this row");

        // Same discrimination as the case above: a blanket refusal on any open position would produce this exact
        // result, so prove the fixture was one positive answer away from adopting. Seeding the execution is the only
        // thing that changes between the two requests.
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey, fixture.OrderId.ToString(), FilledSize, venueOrderKey: FilledVenueKey);

        using HttpResponseMessage retry = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        retry.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the venue now reports the execution, so the same fixture adopts — which is what shows the refusal above "
            + "turned on the ANSWER and not merely on the account being non-flat " + await DescribeAsync(retry));
        (await OrderAsync(fixture.OrderId)).Status.Should().Be(
            OrderStatus.Filled, "a positively reported fill is the one answer that may adopt");
    }

    // =================================================================================================================
    // 3. No stacking window: the adopted row is the one Filled that is NOT flat.
    // =================================================================================================================

    [Fact]
    public async Task EveryEntryPath_ShouldStillBeRefused_AfterAStillOpenFillIsAdoptedOntoTheRow()
    {
        // The hazard gh#723 introduces. Both entry paths run an allow-list that treats Filled as safe to ignore,
        // because a filled order used to imply the account had gone flat again. An adopted still-open fill breaks
        // that implication — it is a Filled row with a LIVE position behind it — so if the allow-list were the only
        // guard, this adopt would open exactly the gh#530/#531 stacking window it is meant to close. The backstop is
        // the flat-account refusal every entry composes against, and this case exists to prove it actually fires.
        Fixture fixture = await StrandATakeAsync("Topstep-769-NoStacking");

        // Armed BEFORE the position exists: arming composes the same flat-account snapshot, so a ticket staged after
        // the seed could never be produced — and the interesting question is what happens to one staged beforehand.
        Guid peerTicket = await ArmAsync(fixture.Client, fixture.AccountId, Proposal("MNQ"));

        VenueFactory.SeedPosition(fixture.VenueAccountKey, OpenContractKey, netQuantity: (int)FilledSize);
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey, fixture.OrderId.ToString(), FilledSize, venueOrderKey: FilledVenueKey);

        using (HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile"))
        {
            reconcile.StatusCode.Should().Be(
                HttpStatusCode.OK, "the adopt must have happened, or this case proves nothing " + await DescribeAsync(reconcile));
        }

        (await OrderAsync(fixture.OrderId)).Status.Should().Be(
            OrderStatus.Filled, "the fixture must leave behind the Filled-but-not-flat row this case is about");

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage peerTake = await PostAsync(fixture.Client, $"/orders/{peerTicket}/take");
        peerTake.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "taking a second entry onto an account that is carrying an open position sizes the R-5 gate against a "
            + "flat snapshot that ignores the live exposure — up to twice the approved risk (gh#531) "
            + await DescribeAsync(peerTake));
        (await ErrorOfAsync(peerTake)).Should().Contain(
            "open position",
            "the refusal must come from the FLATNESS check, not incidentally from the status allow-list — the "
            + "allow-list deliberately lets Filled through, so weakening the flatness refusal would silently reopen "
            + "the stacking window with every status assertion still green");

        using HttpResponseMessage directSend = await fixture.Client.PostAsJsonAsync(
            $"/accounts/{fixture.AccountId}/orders", Proposal("MNQ"));
        directSend.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "the direct send composes the same flat-account snapshot as the take, so it must refuse for the same "
            + "reason — a guard on one entry path only is not a guard " + await DescribeAsync(directSend));

        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore,
            "the decisive fact is that NOTHING reached the venue: a 409 that had already transmitted would leave a "
            + "second live order on the account whatever the response body said");
    }

    [Fact]
    public async Task Reconcile_ShouldHoldTheAccountEntryLock_SoAConcurrentTakeCannotComposeWhileItResolves()
    {
        // The status assertions above all read the account AFTER the reconcile committed. That cannot see the window
        // the reconcile is itself open for: between reading venue truth and committing the adopt, a concurrent take
        // composes its own flat-account snapshot, and if it is not blocked it decides against a state the reconcile
        // is about to invalidate. So the peer is launched from INSIDE the reconcile — mid-flight, before it has
        // written anything — and the load-bearing assertion is that it had NOT finished while the reconcile was
        // still in there. It is sampled, never awaited: awaiting would deadlock on the very lock under test.
        Fixture fixture = await StrandATakeAsync("Topstep-769-EntryLock");
        Guid peerTicket = await ArmAsync(fixture.Client, fixture.AccountId, Proposal("MNQ"));

        VenueFactory.SeedPosition(fixture.VenueAccountKey, OpenContractKey, netQuantity: (int)FilledSize);
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey, fixture.OrderId.ToString(), FilledSize, venueOrderKey: FilledVenueKey);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        Task<HttpResponseMessage>? peerCall = null;
        bool peerFinishedInsideTheReconcile = false;
        bool launched = false;

        VenueFactory.OnFillHistoryRead(async () =>
        {
            if (launched)
            {
                return;
            }

            launched = true;
            peerCall = PostAsync(fixture.Client, $"/orders/{peerTicket}/take");
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            peerFinishedInsideTheReconcile = peerCall.IsCompleted;
        });

        // The reset is UNCONDITIONAL: the venue factory lives on the class fixture, so a callback left armed by a
        // throwing request would leak into whichever test runs next and silently hijack its fill-history read.
        try
        {
            using HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");
            reconcile.StatusCode.Should().Be(
                HttpStatusCode.OK, "the adopt must complete, or the interleave proves nothing " + await DescribeAsync(reconcile));
        }
        finally
        {
            VenueFactory.OnFillHistoryRead(null);
        }

        peerCall.Should().NotBeNull("the interleave must have run — otherwise nothing was raced");

        peerFinishedInsideTheReconcile.Should().BeFalse(
            "the concurrent take must still have been WAITING when the reconcile was mid-flight — a take that got "
            + "all the way to a decision in there evaluated the account against truth the reconcile had already "
            + "invalidated, which is the per-account entry lock (gh#531/#589) not being held across this path");

        using HttpResponseMessage peer = await peerCall!;
        peer.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "once it is finally let in, the take meets the adopted position and is refused " + await DescribeAsync(peer));
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "and it must never have reached the venue — serialised, then refused, transmitting nothing");
    }

    // =================================================================================================================
    // 4. Positive control for the seam itself, on the branch that already ships (gh#631).
    // =================================================================================================================

    [Fact]
    public async Task Reconcile_ShouldJournalTheRowAsFilled_WhenTheTakeFilledAndRoundTrippedToFlat()
    {
        // Not gh#723 — this is its already-shipped sibling gh#631: nothing rests, the account IS flat, but the tag
        // shows an execution, so the take placed, filled and its bracket closed the position. It is here as a
        // POSITIVE CONTROL for the new seam: it exercises SeedTaggedFill against behaviour that is already live, so
        // a red on the gh#723 cases above is unambiguously the missing adopt rather than a mis-wired stub. Without
        // it, a seam that never reached FindFilledOrderByTagAsync at all would look exactly like an absent feature.
        Fixture fixture = await StrandATakeAsync("Topstep-769-RoundTripped");

        // No position seeded: the account is provably flat, which is what makes this the round-tripped case.
        VenueFactory.SeedTaggedFill(
            fixture.VenueAccountKey, fixture.OrderId.ToString(), FilledSize, venueOrderKey: FilledVenueKey);

        using HttpResponseMessage reconcile = await PostAsync(fixture.Client, $"/orders/{fixture.OrderId}/reconcile");

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK, "a flat account with a confirmed fill is resolvable " + await DescribeAsync(reconcile));

        Order journaled = await OrderAsync(fixture.OrderId);
        journaled.Status.Should().Be(
            OrderStatus.Filled,
            "the trade really happened, so releasing the ticket to Staged would silently discard it from the journal "
            + "the system reasons from (R-8/R-9, gh#631)");
        journaled.VenueOrderKey.Should().Be(
            FilledVenueKey, "the venue's own handle is what ties this row to the execution behind it");
        (await StopPlanCountAsync(fixture.OrderId)).Should().Be(
            0, "the position has already round-tripped — there is nothing left to protect");
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

    /// <summary>Everything one case needs about the strand it is reconciling.</summary>
    private sealed record Fixture(
        HttpClient Client,
        Guid AccountId,
        string VenueAccountKey,
        Guid OrderId,
        Guid SuggestionId,
        DateTimeOffset AttemptedAt);

    /// <summary>
    /// Produces the strand this suite reconciles, the way production produces it: arm a suggestion-provenanced
    /// ticket, then fault the venue seam with a <b>maybe-live</b> refusal so the take cannot conclude the order is
    /// dead and must leave the row <see cref="OrderStatus.Taking"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strand is asserted here rather than assumed: a change that stopped stranding would otherwise make every
    /// case above reconcile a row that was never stranded, and they would go green having tested nothing.
    /// </para>
    /// <para>
    /// The suggestion is seeded and its id stamped onto the armed row directly. The provenance is an <i>input</i> to
    /// the subject under test (does the adopt journal a disposition?), not part of it, and routing through
    /// <c>POST /suggestions/{id}/take</c> would drag this suite's instrument, spec and gate parameters along with
    /// it. The take that strands is the real one either way.
    /// </para>
    /// </remarks>
    private async Task<Fixture> StrandATakeAsync(string firmName)
    {
        VenueFactory.ResetPositions();

        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, string venueAccountKey) = await SetupTradeableAccountAsync(client, firmName);
        await DeclareRiskProfileAsync(client, accountId);

        Guid orderId = await ArmAsync(client, accountId, Proposal("MES"));
        Guid suggestionId = await StampSuggestionProvenanceAsync(accountId, orderId);

        VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
            "accepted but returned no order id", VenueRefusalKind.Indeterminate));

        DateTimeOffset attemptedAt = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage take = await PostAsync(client, $"/orders/{orderId}/take");
            take.StatusCode.Should().NotBe(
                HttpStatusCode.OK, "a maybe-live fault must never report the take as succeeded");
        }
        catch (Exception)
        {
            // Tolerated: the property under test is the durable DB state, not whether the fault escaped the request.
        }

        VenueFactory.ClearPlaceOrderFaults();

        Order stranded = await OrderAsync(orderId);
        stranded.Status.Should().Be(
            OrderStatus.Taking,
            "the fixture must actually produce the strand this suite reconciles — otherwise every case below would "
            + "be reconciling a row that was never stranded");
        stranded.VenueOrderKey.Should().BeNull(
            "the strand is precisely the state where the venue's answer was never journaled");
        (await DispositionCountAsync(suggestionId)).Should().Be(
            0,
            "the faulted take journaled no disposition, so a disposition found after the reconcile can only have "
            + "been written BY the reconcile");

        return new Fixture(client, accountId, venueAccountKey, orderId, suggestionId, attemptedAt);
    }

    /// <summary>Asserts the row was left exactly as the strand left it — not adopted, not released, nothing journaled.</summary>
    private async Task AssertStillStrandedAsync(Fixture fixture, string because)
    {
        Order row = await OrderAsync(fixture.OrderId);

        row.Status.Should().Be(OrderStatus.Taking, because);
        row.VenueOrderKey.Should().BeNull(
            "no venue handle may be stamped on a row the venue never positively attributed to this take — a wrong "
            + "key is worse than none, because cancel / flatten would then act on somebody else's order");
        row.Size.Should().Be(ArmedSize, "nothing was confirmed, so the armed size stands unchanged");
        (await StopPlanCountAsync(fixture.OrderId)).Should().Be(0, "a refusal writes no protection plan");
        (await DispositionCountAsync(fixture.SuggestionId)).Should().Be(
            0,
            "a refused reconcile must journal NO take: a disposition here would tell the R-9 loop the operator "
            + "executed a trade the system could not even confirm happened");
    }

    /// <summary>
    /// Seeds a suggestion matching the armed ticket exactly and stamps it onto the row, so the adopt has a
    /// disposition to journal. Every parameter matches, so an unmodified take records
    /// <see cref="SuggestionDispositionKind.Taken"/> rather than <c>Modified</c>.
    /// </summary>
    private async Task<Guid> StampSuggestionProvenanceAsync(Guid accountId, Guid orderId)
    {
        Guid suggestionId = Guid.NewGuid();
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-5);

        await ExecuteDbContextAsync(async database =>
        {
            Order armed = await database.Orders.IgnoreQueryFilters().SingleAsync(row => row.Id == orderId);

            // Every field is mirrored off the armed row so an unmodified take records `Taken` rather than `Modified`.
            // The target is the one that can be absent, and a suggestion's is non-nullable — so a staged row that
            // dropped it would silently turn every disposition assertion in this suite into a deviation, which is a
            // confusing failure a long way from its cause. Say it here instead.
            armed.TakeProfitPrice.Should().NotBeNull(
                "the fixture's proposal carries a winning-side target, and the disposition can only read as unmodified "
                + "if the armed row kept it");

            database.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = armed.UserId,
                AccountId = accountId,
                Instrument = "MES", // the neutral SYMBOL; the contract is resolved venue-side
                Side = armed.Side,
                Size = armed.Size,
                EntryPrice = armed.EntryPrice,
                StopPrice = armed.WorkingStopPrice,
                TargetPrice = armed.TakeProfitPrice!.Value,
                Mode = armed.Mode, // ct_suggestions_mode_matches_account raises unless these agree
                State = SuggestionState.Active,
                CreatedAt = created,
                Rationale = "seeded so the adopt has a disposition to journal (gh#769)",
                CitedIndicator = "rsi",
                CitedPeriod = 14,
                CitedResolutionMinutes = 5,
                Confidence = 60,
                ExpiresAt = created.AddHours(1), // CK_Suggestions_ExpiresAfterCreated
            });

            armed.SuggestionId = suggestionId;
            await database.SaveChangesAsync();
        });

        return suggestionId;
    }

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route) =>
        client.PostAsync(route, content: null);

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
    private async Task<(Guid AccountId, string VenueAccountKey)> SetupTradeableAccountAsync(
        HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"{firmName}-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await PostAsync(client, $"/connections/{connection.Id}/accounts/discover");
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(account => account.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
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

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
    }

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the fixture's proposal must arm cleanly " + await DescribeAsync(response));
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    /// <summary>A long carrying a winning-side target, so the seeded suggestion can match it field for field.</summary>
    private static SendOrderRequest Proposal(string symbol) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: ArmedSize,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market,
        Target: 5_010m);

    private Task<Order> OrderAsync(Guid orderId) => QueryDbContextAsync(database => database.Orders
        .IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == orderId));

    private Task<int> StopPlanCountAsync(Guid orderId) => QueryDbContextAsync(database => database.StopPlans
        .IgnoreQueryFilters().CountAsync(plan => plan.OrderId == orderId));

    private Task<int> DispositionCountAsync(Guid suggestionId) => QueryDbContextAsync(database => database.SuggestionDispositions
        .IgnoreQueryFilters().CountAsync(disposition => disposition.SuggestionId == suggestionId));

    private Task<SuggestionDisposition> DispositionAsync(Guid suggestionId) => QueryDbContextAsync(database => database.SuggestionDispositions
        .IgnoreQueryFilters().AsNoTracking().SingleAsync(disposition => disposition.SuggestionId == suggestionId));

    private async Task<string> ErrorOfAsync(HttpResponseMessage response)
    {
        ErrorResponse? body = await response.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOptions);
        return body?.Error ?? string.Empty;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"(response was {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()})";

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    private async Task<T> QueryDbContextAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }
}
