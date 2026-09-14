using System.Net;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Post-merge staging gate (gh#269 ⇒ gh#259).</b> Verifies the gh#259 reprice safety claim that <i>only the real
/// ProjectX gateway can witness</i>: that the <b>attached protective bracket is preserved</b> when a working entry is
/// modified in place. The app cannot re-assert it (the client <c>ModifyOrderRequest</c> carries no bracket field) and
/// exposes no resting-orders read of its own, so this reads <b>venue truth directly from the gateway</b>
/// (<see cref="StagingProjectXGateway"/>) — a fake venue proves nothing. It SKIPS pre-merge via
/// <see cref="StagingGatewayFactAttribute"/> and is serialized onto the reserved practice account.
/// </summary>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class BracketPreservationStagingIntegrationTests : StagingVenueExecutionSuite
{
    [StagingVenueFact]
    public async Task PracticeAccount_ShouldResolveThroughTheDeployedApi_ForTheModifyGate()
    {
        // The execution-gate precondition: the reserved practice account is discoverable through the deployed API.
        // If this fails, the gate below cannot run — so it is asserted first and on its own.
        using HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(client);

        accountId.Should().NotBe(Guid.Empty, "the reserved practice account resolved on staging");
    }

    /// <summary>
    /// The gh#269 safety gate proper — the assumption gh#259 rests on, certified against the real gateway. Place a
    /// bracketed working entry, reprice it <b>price-only</b> in place (the gh#259 operation), let it fill, and read
    /// venue truth: a native protective stop must still rest, covering the position. A gateway that drops the attached
    /// bracket on a modify leaves the fill with <b>no</b> protective leg — the exact hazard the always-native safety
    /// stop exists to prevent (R-5 / R-11 / ADR-0007) — so the resting stop is <b>absent</b> and this test goes red.
    /// </summary>
    /// <remarks>
    /// Only the gateway can witness this, so the suite runs post-merge on staging (QA contract §1). The live-market
    /// fill choreography (repricing the resting entry to marketable) is exercised — and, if the venue needs a
    /// different trigger, tuned — on the first staging run, per the DRAFT-lineage of this gate.
    /// </remarks>
    [StagingGatewayFact]
    public async Task Modify_ShouldPreserveTheAttachedProtectiveBracket_WhenTheEntryIsRepriced()
    {
        const int size = 1;
        string symbol = StagingConfig.ExecutionInstrument!;

        using HttpClient app = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(app);
        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();
        ClientModels.Contract contract = await gateway.ResolveContractAsync(symbol);
        decimal pointValue = contract.TickSize == 0m ? contract.TickValue : contract.TickValue / contract.TickSize;

        // Start flat — the collection serializes, but a prior crash could have left residue on the shared account.
        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        await DeclareRiskAsync(app, accountId, maxContractsPerOrder: size);
        decimal market = await gateway.ReferencePriceAsync(contract.Id);

        // 1. Place a resting long (Working, below the market) carrying its always-native safety bracket.
        SendOrderResponse placed = await PlaceRestingLongAsync(
            app, accountId, symbol, contract.TickSize, pointValue, size, market);
        placed.OrderId.Should().NotBeNull("the bracketed entry was accepted and journaled as Working");

        // 2. The operation under test: a PRICE-ONLY in-place modify (gh#259) to another below-market price — it stays
        //    Working, and the safety stop is held invariant by the app.
        decimal repriced = market - (30m * contract.TickSize);
        using HttpResponseMessage modify = await ModifyAsync(
            app, placed.OrderId!.Value, new ModifyWorkingOrderRequest(EntryPrice: repriced, ReferencePrice: repriced));
        modify.StatusCode.Should().Be(HttpStatusCode.OK, "a price-only reprice of a working order is accepted (gh#259)");

        // 3. Realize the fill so the on-fill bracket materialises — reprice the (already-modified) entry to marketable.
        //    The app may answer Conflict if the fill lands mid-modify (benign); venue truth is the oracle, not the app.
        decimal marketable = market + (40m * contract.TickSize);
        using HttpResponseMessage _ = await ModifyAsync(
            app, placed.OrderId!.Value,
            new ModifyWorkingOrderRequest(EntryPrice: marketable, ReferencePrice: marketable));
        bool filled = await WaitUntilAsync(
            () => gateway.HasOpenPositionAsync(gatewayAccountId, symbol), TimeSpan.FromSeconds(30));
        filled.Should().BeTrue("the repriced-to-marketable entry filled on the practice account");

        // 4. Venue truth: the attached protective bracket SURVIVED the in-place modify — a native stop still rests,
        //    covering the position, on the protective side of a long entry.
        IReadOnlyList<ClientModels.Order> open = await gateway.OpenOrdersAsync(gatewayAccountId);
        ClientModels.Order? protectiveStop = StagingProjectXGateway.ProtectiveStop(open, symbol);
        protectiveStop.Should().NotBeNull(
            "a native protective stop must rest after the fill — the attached bracket was preserved across the modify (gh#269)");
        protectiveStop!.Size.Should().Be(size, "the preserved bracket still covers the whole position — none of it is naked");
        protectiveStop.StopPrice.Should().NotBeNull("a protective stop leg carries a trigger");

        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
    }
}
