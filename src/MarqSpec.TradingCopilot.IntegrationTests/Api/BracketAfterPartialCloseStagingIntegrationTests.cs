using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Hard pre-live staging gate (gh#1012 ⇒ gh#928).</b> The sibling of gh#293: that gate certified bracket
/// <i>sizing</i> across a working-order <b>resize</b>; this one certifies bracket <i>behaviour</i> across a position
/// <b>partial close</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> The always-native safety bracket carries <b>no size field</b> (<c>PlaceOrderBracket =
/// {ticks, type}</c>) — the gateway attaches it on fill, sized to the realized fill (gh#293). Nothing in the copilot
/// can then resize it: the client's modify request has no bracket field. So if a position is reduced underneath a
/// bracket the gateway does <b>not</b> auto-manage, the resting protective stop still covers the <i>original</i>
/// quantity. Reduce long-2 → long-1 and a stop still sized 2 would, on trigger, sell 2 against a 1-long —
/// <b>overshooting into a short-1</b>. A protective mechanism would have <i>created</i> exposure, which is the exact
/// class of failure the always-native stop exists to remove (R-11 / R-13 / ADR-0007). The mirror image is no better:
/// a gateway that <b>cancels</b> the bracket leaves the surviving remainder <b>naked</b>.
/// </para>
/// <para>
/// <b>Only the real gateway can answer this.</b> Whether ProjectX auto-reduces a position-linked OCO bracket on
/// <c>partialCloseContract</c> is not derivable from the vendored <c>swagger.json</c>, the client models, or any unit
/// test — the swagger describes the bracket's <i>shape</i>, never its lifecycle under a partial close. It is a live
/// venue characteristic, so this reads <b>venue truth directly from the gateway</b>
/// (<see cref="StagingProjectXGateway"/>), whose open-orders endpoint is the only surface that reports a resting
/// leg's <c>Size</c> at all (the app's venue-neutral <c>WorkingOrder</c> view drops it).
/// </para>
/// <para>
/// <b>Status — scaffolded, NOT yet run (the finding is still open).</b> These cases assert the <b>only</b> outcome
/// that clears the gate: the gateway auto-reduces every attached leg to the surviving remainder. That assertion is
/// deliberate, not optimistic — the gate's job is to make the safe behaviour the thing that must be demonstrated. On
/// the first staging run with practice credentials, exactly one of two things happens, and both are the deliverable:
/// <list type="bullet">
/// <item><b>Green</b> → the gateway auto-manages the bracket, and gh#928's reduce is safe on the bracket axis.</item>
/// <item><b>Red</b> → the finding that <b>defines the follow-up</b>: gh#928's reduce must itself resize or cancel the
/// bracket (or refuse a reduce that would desync it) before any funded use. Pin the observed size per the QA
/// contract's rule 2 and open the <c>work:code</c> card.</item>
/// </list>
/// Until then <b>no outcome has been observed</b>, and nothing downstream may claim the gate is cleared.
/// </para>
/// <para>
/// <b>The dependency runs the other way round from gh#1012's framing.</b> gh#1012 describes the reduce as already
/// shipped and this gate as blocking only funded use. It is not shipped: there is no
/// <c>IOrderExecutor.ReducePositionAsync</c>, no <c>VenueCapability.ReducePosition</c>, no
/// <c>PositionReduceService</c>, and no <c>POST /accounts/{id}/positions/{instrument}/reduce</c> anywhere in the
/// tree, and gh#928 is open with its backend increment unchecked and explicitly "<i>Blocked on the §⚠️ practice
/// verification + ratification before it lands</i>". This suite is therefore a <b>prerequisite to gh#928 landing</b>,
/// which is what gh#928 itself asks for. That also fixes what the partial close is issued through: with no app
/// reduce path to drive, the close goes straight to the gateway (see
/// <see cref="StagingProjectXGateway.PartialCloseAsync"/>) — the entry is still placed through the deployed app and
/// its real risk gate.
/// </para>
/// <para>
/// SKIPS pre-merge via <see cref="StagingGatewayFactAttribute"/>; serialized onto the reserved PRACTICE account
/// (R-14).
/// </para>
/// </remarks>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class BracketAfterPartialCloseStagingIntegrationTests : StagingVenueExecutionSuite
{
    /// <summary>How many contracts the entry opens, and how many of them the partial close takes off.</summary>
    private const int EntrySize = 2;
    private const int CloseSize = 1;
    private const int Remainder = EntrySize - CloseSize;

    /// <summary>
    /// <b>Long.</b> Fill long-2 under a native protective stop sized 2, <c>partialCloseContract</c> 1, then read the
    /// resting stop. Prove-the-red: a gateway that leaves the stop at <b>2</b> fails the size assertion — that stop
    /// sells 2 against a 1-long and <b>flips the account short</b>; a gateway that <b>cancels</b> it fails the
    /// not-null assertion — the surviving long-1 is left <b>naked</b>. Only an auto-reduce to 1 is green.
    /// </summary>
    [StagingGatewayFact]
    public async Task PartialClose_ShouldAutoReduceTheRestingProtectiveStopToTheRemainder_WhenALongIsPartiallyClosed() =>
        await AssertBracketTracksTheRemainderAsync(isLong: true, withTakeProfit: false);

    /// <summary>
    /// <b>Short.</b> The direction-symmetric case: an over-sized <i>buy</i>-stop against a reduced short overshoots
    /// into an unwanted <b>long</b>. Same prove-the-red as the long case, mirrored — this exists because a gateway
    /// could plausibly auto-manage one side's bracket and not the other, and a gate that only ever tested longs
    /// would certify a reduce that is unsafe on every short.
    /// </summary>
    [StagingGatewayFact]
    public async Task PartialClose_ShouldAutoReduceTheRestingProtectiveStopToTheRemainder_WhenAShortIsPartiallyClosed() =>
        await AssertBracketTracksTheRemainderAsync(isLong: false, withTakeProfit: false);

    /// <summary>
    /// <b>The take-profit (OCO) leg.</b> Same flow with a profit target attached, so <i>both</i> bracket legs rest.
    /// The TP leg carries the identical hazard in the opposite direction — an over-sized take-profit fills 2 against a
    /// 1-long and flips short at the target, an outcome an operator would read as a <i>win</i> while it silently
    /// opens an unwanted position. Prove-the-red: a stop-only auto-reduce (the gateway managing one leg of the OCO
    /// pair but not the other) leaves the TP at 2 and reddens here while the long case above stays green — which is
    /// precisely the partial behaviour a single-leg gate would miss.
    /// </summary>
    [StagingGatewayFact]
    public async Task PartialClose_ShouldAutoReduceBothBracketLegsToTheRemainder_WhenABracketedLongIsPartiallyClosed() =>
        await AssertBracketTracksTheRemainderAsync(isLong: true, withTakeProfit: true);

    /// <summary>
    /// The shared gh#1012 flow: place a bracketed resting entry through the deployed app, fill it, assert the
    /// baseline (position and bracket both at <see cref="EntrySize"/>), <c>partialCloseContract</c>
    /// <see cref="CloseSize"/> at the gateway, then read venue truth — every resting leg must now cover
    /// <see cref="Remainder"/> exactly.
    /// </summary>
    /// <remarks>
    /// The baseline assertions are load-bearing, not ceremony: if the bracket never rested at
    /// <see cref="EntrySize"/> to begin with, a post-close reading of <see cref="Remainder"/> would be a
    /// <b>false pass</b> — the suite would certify an auto-reduce that never happened. Asserting the before-state is
    /// what makes the after-state evidence of the transition. The live-market fill choreography mirrors gh#293's and
    /// is tuned, if the venue needs a different trigger, on the first staging run.
    /// </remarks>
    private async Task AssertBracketTracksTheRemainderAsync(bool isLong, bool withTakeProfit)
    {
        string symbol = StagingConfig.ExecutionInstrument!;

        using HttpClient app = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(app);
        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();
        ClientModels.Contract contract = await gateway.ResolveContractAsync(symbol);
        decimal pointValue = contract.TickSize == 0m ? contract.TickValue : contract.TickValue / contract.TickSize;

        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        await DeclareRiskAsync(app, accountId, maxContractsPerOrder: EntrySize);
        decimal market = await gateway.ReferencePriceAsync(contract.Id);

        try
        {
            // 1. Place a resting bracketed entry at the full size. The target, when asked for, sits 40 ticks the
            //    profitable way from the entry so the OCO take-profit leg is attached alongside the stop.
            decimal entry = isLong
                ? market - (40m * contract.TickSize)
                : market + (40m * contract.TickSize);
            decimal? target = withTakeProfit
                ? (isLong ? entry + (40m * contract.TickSize) : entry - (40m * contract.TickSize))
                : null;

            SendOrderResponse placed = isLong
                ? await PlaceRestingLongAsync(
                    app, accountId, symbol, contract.TickSize, pointValue, EntrySize, market, target: target)
                : await PlaceRestingShortAsync(
                    app, accountId, symbol, contract.TickSize, pointValue, EntrySize, market, target: target);
            placed.OrderId.Should().NotBeNull("the bracketed entry was accepted and journaled as Working");

            // 2. Realize the fill — reprice the resting entry to marketable. The app may answer Conflict if the fill
            //    lands mid-modify (benign); venue truth is the oracle, not the app's response.
            decimal marketable = isLong
                ? market + (40m * contract.TickSize)
                : market - (40m * contract.TickSize);
            using HttpResponseMessage _ = await ModifyAsync(
                app, placed.OrderId!.Value,
                new ModifyWorkingOrderRequest(EntryPrice: marketable, ReferencePrice: marketable));

            bool filled = await WaitUntilAsync(
                async () => Math.Abs(await gateway.NetQuantityAsync(gatewayAccountId, symbol)) == EntrySize,
                TimeSpan.FromSeconds(30));
            filled.Should().BeTrue("the repriced-to-marketable entry filled the full size on the practice account");

            // 3. Baseline — the bracket rests at the FULL size before anything is closed. Without this, a post-close
            //    reading of the remainder could never distinguish an auto-reduce from a bracket that was under-sized
            //    all along.
            int signedBefore = await gateway.NetQuantityAsync(gatewayAccountId, symbol);
            signedBefore.Should().Be(isLong ? EntrySize : -EntrySize, "the fill opened the full position, correctly signed");

            IReadOnlyList<ClientModels.Order> restingBefore = await gateway.OpenOrdersAsync(gatewayAccountId);
            ClientModels.Order? stopBefore = StagingProjectXGateway.ProtectiveStop(restingBefore, symbol);
            stopBefore.Should().NotBeNull("the always-native safety bracket materialised a protective stop on fill");
            stopBefore!.Size.Should().Be(EntrySize, "the on-fill bracket sized to the realized fill (the gh#293 result)");

            if (withTakeProfit)
            {
                ClientModels.Order? takeProfitBefore = StagingProjectXGateway.ProtectiveTakeProfit(restingBefore, symbol);
                takeProfitBefore.Should().NotBeNull("the profit target materialised the OCO take-profit leg on fill");
                takeProfitBefore!.Size.Should().Be(EntrySize, "the take-profit leg also sized to the realized fill");
            }

            // 4. The operation under test: partially close the POSITION at the gateway. Nothing here touches the
            //    bracket — that is the whole question.
            ClientModels.PartialClosePositionResponse closed = await gateway.PartialCloseAsync(
                gatewayAccountId, contract.Id, CloseSize);
            closed.Success.Should().BeTrue(
                "partialCloseContract accepted the sized close (error code {0})", closed.ErrorCode);

            bool reduced = await WaitUntilAsync(
                async () => Math.Abs(await gateway.NetQuantityAsync(gatewayAccountId, symbol)) == Remainder,
                TimeSpan.FromSeconds(30));
            reduced.Should().BeTrue("the partial close left exactly the remainder open at the venue");

            // 5. THE GATE. Venue truth after the reduce: every resting protective leg must now cover the remainder.
            IReadOnlyList<ClientModels.Order> restingAfter = await gateway.OpenOrdersAsync(gatewayAccountId);
            ClientModels.Order? stopAfter = StagingProjectXGateway.ProtectiveStop(restingAfter, symbol);

            stopAfter.Should().NotBeNull(
                "a partial close must not leave the surviving remainder naked — a cancelled bracket is a gate failure "
                + "(gh#1012), not a pass");
            stopAfter!.Size.Should().Be(Remainder,
                "the gateway must auto-reduce the position-linked bracket to the surviving quantity; a stop still "
                + "sized {0} against a {1}-lot position overshoots on trigger and OPENS an opposing position "
                + "(gh#1012 ⇒ gh#928)", EntrySize, Remainder);

            if (withTakeProfit)
            {
                ClientModels.Order? takeProfitAfter = StagingProjectXGateway.ProtectiveTakeProfit(restingAfter, symbol);
                takeProfitAfter.Should().NotBeNull(
                    "the OCO take-profit leg must survive a partial close of the position it protects");
                takeProfitAfter!.Size.Should().Be(Remainder,
                    "the take-profit leg must auto-reduce with its OCO sibling; an over-sized target fills {0} against "
                    + "a {1}-lot position and flips the account at the target (gh#1012)", EntrySize, Remainder);
            }
        }
        finally
        {
            // Always return the reserved account flat — a serialized rerun starts from a known state, and a leg left
            // resting on a practice account after a failed assertion is a real position.
            await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        }
    }
}
