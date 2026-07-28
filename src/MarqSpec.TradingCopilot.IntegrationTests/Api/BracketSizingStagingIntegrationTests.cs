using System.Net;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Hard pre-live staging gate (gh#293 ⇒ gh#292).</b> The resize feature relaxes the modify family's size-invariant
/// on a <i>structural inference</i>: the ProjectX safety bracket carries no size field and is instantiated on fill, so
/// the on-fill protective stop <i>should</i> size to the realized fill. On a safety-critical path an inference is not
/// a certification — a gateway that instead pinned the bracket to the placement-time parent size would leave a
/// resize-<b>up</b> partly naked on fill, or a resize-<b>down</b> with an oversized residual that reverses into a
/// naked position after close. Only the real gateway can certify this, and the deployed app surfaces no bracket
/// read (its <c>WorkingOrder</c> view drops the size), so this reads <b>venue truth directly from the gateway</b>
/// (<see cref="StagingProjectXGateway"/>). It SKIPS pre-merge via <see cref="StagingGatewayFactAttribute"/> and is
/// serialized onto the reserved practice account.
/// </summary>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class BracketSizingStagingIntegrationTests : StagingVenueExecutionSuite
{
    [StagingVenueFact]
    public async Task PracticeAccount_ShouldResolveThroughTheDeployedApi_ForTheResizeGate()
    {
        // Precondition for the resize gate: the reserved practice account is discoverable through the deployed API.
        using HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(client);

        accountId.Should().NotBe(Guid.Empty, "the reserved practice account resolved on staging");
    }

    /// <summary>
    /// Resize UP while working, then fill → the on-fill protective stop covers the <b>full realized (larger) fill</b>,
    /// no naked excess (the gh#293 gate). Prove-the-red: a gateway that pins the bracket to the placement-time size
    /// leaves the resized-up excess unprotected — the resting stop's size is the <i>old</i> quantity, and this fails.
    /// </summary>
    [StagingGatewayFact]
    public async Task Resize_ShouldSizeTheOnFillBracketToTheRealizedFill_WhenAWorkingOrderIsResizedUp()
    {
        const int startSize = 1;
        const int resizedSize = 2;
        await AssertOnFillBracketMatchesResizedFillAsync(startSize, resizedSize);
    }

    /// <summary>
    /// Resize DOWN while working, then fill → the on-fill protective stop covers the <b>smaller fill exactly</b>, no
    /// oversized residual that could reverse into a naked position after the position closes (the gh#293 gate).
    /// Prove-the-red: a gateway that pins the bracket to the placement-time size leaves a stop sized to the <i>old</i>
    /// (larger) quantity — an oversized residual — and this fails.
    /// </summary>
    [StagingGatewayFact]
    public async Task Resize_ShouldCoverTheSmallerFillExactly_WhenAWorkingOrderIsResizedDown()
    {
        const int startSize = 2;
        const int resizedSize = 1;
        await AssertOnFillBracketMatchesResizedFillAsync(startSize, resizedSize);
    }

    /// <summary>
    /// The shared gh#293 flow: place a bracketed working long at <paramref name="startSize"/>, resize it to
    /// <paramref name="resizedSize"/> while working (the #292 capability), fill it, then read venue truth — the
    /// position and its native protective stop must both equal the resized quantity. The resize and the fill are two
    /// steps so the resize is asserted on its own before the fill realizes it (the AC's "resize while working, then
    /// fill"). Only the gateway certifies the on-fill sizing, so this runs post-merge on staging; the live-market
    /// fill choreography is exercised — and tuned if the venue needs a different trigger — on the first staging run.
    /// </summary>
    private async Task AssertOnFillBracketMatchesResizedFillAsync(int startSize, int resizedSize)
    {
        string symbol = StagingConfig.ExecutionInstrument!;
        int maxContracts = Math.Max(startSize, resizedSize);

        using HttpClient app = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(app);
        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();
        ClientModels.Contract contract = await gateway.ResolveContractAsync(symbol);
        decimal pointValue = contract.TickSize == 0m ? contract.TickValue : contract.TickValue / contract.TickSize;

        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        await DeclareRiskAsync(app, accountId, maxContractsPerOrder: maxContracts);
        decimal market = await gateway.ReferencePriceAsync(contract.Id);

        // 1. Place a resting long at the start size (Working, below the market) with its always-native safety bracket.
        SendOrderResponse placed = await PlaceRestingLongAsync(
            app, accountId, symbol, contract.TickSize, pointValue, startSize, market);
        placed.OrderId.Should().NotBeNull("the bracketed entry was accepted and journaled as Working");

        // 2. The operation under test: resize the working order in place (gh#292) — size only, still below the market.
        using HttpResponseMessage resize = await ModifyAsync(
            app, placed.OrderId!.Value, new ModifyWorkingOrderRequest(EntryPrice: null, Size: resizedSize));
        resize.StatusCode.Should().Be(HttpStatusCode.OK, "an in-place resize of a working order is accepted (gh#292)");

        // 3. Realize the fill at the resized size — reprice to marketable. The app may answer Conflict if the fill
        //    lands mid-modify (benign); venue truth is the oracle, not the app's response.
        decimal marketable = market + (40m * contract.TickSize);
        using HttpResponseMessage _ = await ModifyAsync(
            app, placed.OrderId!.Value,
            new ModifyWorkingOrderRequest(EntryPrice: marketable, ReferencePrice: marketable));
        bool filled = await WaitUntilAsync(
            () => gateway.HasOpenPositionAsync(gatewayAccountId, symbol), TimeSpan.FromSeconds(30));
        filled.Should().BeTrue("the repriced-to-marketable entry filled on the practice account");

        // 4. Venue truth: the position opened at the RESIZED quantity...
        IReadOnlyList<ClientModels.Position> positions = await gateway.OpenPositionsAsync(gatewayAccountId);
        ClientModels.Position position = positions.Single(candidate =>
            candidate.ContractId.Contains(symbol, StringComparison.OrdinalIgnoreCase));
        position.Size.Should().Be(resizedSize, "the fill realized the resized quantity");

        // ...and the attached-on-fill protective stop covers that realized fill EXACTLY — the gh#293 gate. Fewer than
        // the fill leaves the excess naked (resize-up); more leaves an oversized residual that reverses after close
        // (resize-down). Either is the catastrophe the always-native safety stop exists to prevent.
        IReadOnlyList<ClientModels.Order> open = await gateway.OpenOrdersAsync(gatewayAccountId);
        ClientModels.Order? protectiveStop = StagingProjectXGateway.ProtectiveStop(open, symbol);
        protectiveStop.Should().NotBeNull("a native protective stop rests after the fill (the always-native safety bracket)");
        protectiveStop!.Size.Should().Be(resizedSize,
            "the on-fill bracket sizes to the realized fill — not the placement-time parent size (gh#293)");

        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
    }
}
