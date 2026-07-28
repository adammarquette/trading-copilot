using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>modify-working-order family</b> via <c>PATCH /orders/{id}/price</c>. The
/// entry-reprice increment (gh#283 ⇒ gh#259) proves a modify is a <b>re-decision, not a rubber stamp</b> — the
/// repriced order re-gates from scratch (R-12), the venue is touched <b>only after</b> the gate re-passes, the change
/// is audited, and a modify that would violate risk or cross its stops is refused. The <b>beyond-reprice</b>
/// increments (gh#387) then guard the rest of the handler: the <b>working-stop re-stage</b> (gh#267) is a purely
/// <b>local</b> write (the hidden stop is a promotion target, not a venue order — the venue is never contacted); the
/// <b>combined entry + working-stop move</b> (gh#278) commits both atomically with the full <c>safety → working →
/// entry</c> chain re-validated <b>before</b> the venue; and the <b>resize</b> (gh#292) re-gates at the new size and
/// transmits the <b>gate-approved</b> quantity, only on a 0-filled <c>Working</c> order. Driven over HTTP with the
/// adversarial venue stub recording every in-place modify; the always-on hosts are stripped by
/// <see cref="OcoExitTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// A modify CAN add risk (unlike a cancel), so it runs the full send ladder and re-gates before any venue call.
/// Working orders (and, where the path coordinates one, their hidden <c>StopPlanRecord</c>) are seeded directly with
/// a known venue handle — the endpoint needs only the row(s) + account/connection + a declared risk profile to
/// resolve the venue and re-decide. The always-native <b>safety stop</b> is invariant on every path. The venue-side
/// bracket preservation/sizing is the separate staging gate (gh#269/#293); this suite guards the venue-independent
/// re-gate, ordering re-validation, local re-stage, and gate-approved-quantity echo pre-merge. Traces R-11 · R-12 ·
/// R-5 · R-20 · ADR-0007.
/// </remarks>
public class ModifyWorkingOrderIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const string VenueKey = "WORK-1";
    private const decimal Entry = 5_000m;
    private const decimal Working = 4_990m;
    private const decimal Safety = 4_980m;

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);
    private sealed record ModifyOkResponse(Guid Id, string Status, decimal EntryPrice, decimal WorkingStopPrice);
    // Only Outcome is asserted; extra wire fields (binding layer, reason) are ignored by System.Text.Json.
    private sealed record RefusalResponse(string Outcome);
    private sealed record ErrorResponse(string? Error);
    // The venue-modify response (resize / combined): size is the GATE-APPROVED quantity, requestedSize the asked one.
    private sealed record VenueModifyResponse(int Size, int? RequestedSize, string Outcome);

    public ModifyWorkingOrderIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Modify_ShouldRepriceEntry_AndReValidate_WhenOrderIsWorking()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ModifyOkResponse? body = await response.Content.ReadFromJsonAsync<ModifyOkResponse>(_jsonOptions);
        body!.EntryPrice.Should().Be(5_008m, "the reprice is reflected in the response");

        (await EntryPriceAsync(orderId)).Should().Be(5_008m, "the repriced entry persists on the order row");
        // The venue is touched via ModifyOrderAsync (in-place), which sits AFTER the risk re-gate in
        // OrderExecutionService.ModifyAsync — so a recorded modify witnesses that the gate re-passed at the
        // unchanged size. Size is transmitted as null ("leave unchanged"); the new entry rides the limit price.
        VenueFactory.ModifyOrderCalls.Should().ContainSingle(
            call => call.VenueOrderKey == VenueKey && call.LimitPrice == 5_008m && call.Size == null,
            "the venue leg is repriced in place, at the unchanged size, only after the gate re-passes");
    }

    [Fact]
    public async Task Modify_ShouldRefuse_WhenRiskTightenedAfterPlacement()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        // A profile that no longer admits the trade's per-contract risk: (5002 - 4980) x $5 x 1 = $110 > $50.
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 50m);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_002m, referencePrice = 5_002m });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "R-12: a modify re-runs the risk gate");
        RefusalResponse? body = await response.Content.ReadFromJsonAsync<RefusalResponse>(_jsonOptions);
        body!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByRisk), "the reprice violated the re-read risk profile");

        (await EntryPriceAsync(orderId)).Should().Be(Entry, "a refused modify leaves the order untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("nothing reaches the venue when the gate refuses");
    }

    [Fact]
    public async Task Modify_ShouldAudit_WhenOrderRepriced()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, referencePrice = 5_008m });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (AuditAction Action, string? Before, string? After) audit = await ModifyAuditAsync();
        audit.Action.Should().Be(AuditAction.OrderModified, "the reprice writes an immutable audit entry");
        // Parsed, not string-compared: the DB numeric scale may render "5000.00" — the value is what matters.
        decimal.Parse(audit.Before!, CultureInfo.InvariantCulture).Should().Be(Entry, "the audit captures the pre-reprice entry");
        decimal.Parse(audit.After!, CultureInfo.InvariantCulture).Should().Be(5_008m, "the audit captures the post-reprice entry");
    }

    [Fact]
    public async Task Modify_ShouldKeepProtectionConsistent_WhenEntryMoves()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        // A Buy entry dropped BELOW both stops would leave the working (4990) and safety (4980) stops ABOVE the
        // entry — protection on the wrong side. The endpoint must refuse on the effective geometry BEFORE the venue,
        // so the CK_StopPlans invariants can never trip after the entry already repriced.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 4_970m, referencePrice = 4_970m });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "an entry that crosses its stops is refused");
        ErrorResponse? body = await response.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOptions);
        body!.Error.Should().Contain("cross", "the refusal is the ordering guard, not a downstream gate or DB failure");

        (await EntryPriceAsync(orderId)).Should().Be(Entry, "the order's entry is untouched");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "protection stays consistent — the working stop is unchanged");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("no crossing reprice reaches the venue");
    }

    [Fact]
    public async Task Modify_ShouldReturn409_WhenOrderIsNotWorking()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey, status: OrderStatus.Filled);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "only a working order has a live resting entry to move");
        (await EntryPriceAsync(orderId)).Should().Be(Entry, "a terminal order is left untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("no venue modify is issued for a non-working order");
    }

    [Fact]
    public async Task Modify_ShouldReturn404_ForAnotherOperator()
    {
        HttpClient owner = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(owner);
        await DeclareRiskProfileAsync(owner, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        HttpClient intruder = await SecondOperatorClientAsync();
        using HttpResponseMessage response = await intruder.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20: another operator cannot see or modify the owner's order");
        (await EntryPriceAsync(orderId)).Should().Be(Entry, "the owner's order is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("nothing is modified at the venue");
    }

    [Fact]
    public async Task Modify_ShouldReturn400_WhenReferencePriceMissing()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        // R-16: the fat-finger band re-measures the new entry against a reference. An entry move without one cannot
        // be sanity-checked, so it is rejected before anything is gated or transmitted.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an entry move requires a reference price (R-16)");
        (await EntryPriceAsync(orderId)).Should().Be(Entry, "the order is untouched when the request is malformed");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("no venue modify is issued for a malformed request");
    }

    // =========================================================================================================
    // gh#267 — re-stage the hidden working stop (a LOCAL write; the venue is never contacted).
    // =========================================================================================================

    [Fact]
    public async Task RestageWorkingStop_ShouldReStageLocally_WithoutTouchingTheVenue_WhenPlanIsHidden()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        // A tighten (4995 is nearer the entry than 4990) — inside the already-approved envelope, so no re-gate.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { workingStopPrice = 4_995m });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a hidden working stop re-stages locally");
        (await WorkingStopAsync(orderId)).Should().Be(4_995m, "the order's working stop moved");
        (await StopPlanAsync(orderId))!.Value.ActualStop.Should().Be(4_995m, "the plan's actual stop moved in lockstep");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty(
            "a hidden working stop is a promotion target, not a venue order — re-staging it touches no venue");
    }

    [Fact]
    public async Task RestageWorkingStop_ShouldRefuse422_WhenNewStopCrossesTheSafetyStop()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        // 4975 sits BELOW the safety stop (4980) — the chain safety < working < entry is broken. The endpoint must
        // refuse on the ordering BEFORE any write, so CK_StopPlans_SafetyBeyondActual can never trip at the DB.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { workingStopPrice = 4_975m });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a working stop beyond the safety stop is refused");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "the working stop is untouched");
        (await StopPlanAsync(orderId))!.Value.ActualStop.Should().Be(Working, "the plan is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("a crossing re-stage reaches no venue");
    }

    [Fact]
    public async Task RestageWorkingStop_ShouldCreateHiddenPlan_WhenOrderWasCoincident()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        // A coincident working/safety pair (both 4980) with NO plan — the shape placement leaves when there is no
        // distinct working stop to hide.
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey, working: Safety);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { workingStopPrice = 4_985m });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "installing a distinct working stop is allowed");
        (StopStaging Staging, decimal ActualStop, decimal EntryPrice)? plan = await StopPlanAsync(orderId);
        plan.Should().NotBeNull("a Hidden plan is created when the coincident pair separates");
        plan!.Value.Staging.Should().Be(StopStaging.Hidden);
        plan.Value.ActualStop.Should().Be(4_985m, "the new plan carries the installed working stop");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("creating a hidden plan touches no venue");
    }

    [Fact]
    public async Task RestageWorkingStop_ShouldRefuse409_WhenPlanIsOrphaned()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Orphaned);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { workingStopPrice = 4_995m });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "an orphaned stop re-arms against venue truth, not a local re-stage");
        (await StopPlanAsync(orderId))!.Value.Staging.Should().Be(StopStaging.Orphaned, "the orphaned plan is untouched");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "the order is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RestageWorkingStop_ShouldReGateAWiden_UnderActualStopBasis()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        // Under ActualStop the per-trade layer is measured at the WORKING stop, so a widen can breach it. A $50
        // per-trade cap: widening to 4982 is (5000-4982) x $5 x 1 = $90 > $50 — the one case a working-stop move re-gates.
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 50m, sizingBasis: SizingBasis.ActualStop);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { workingStopPrice = 4_982m });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "a widen under ActualStop re-gates the per-trade layer — this one breaches it");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "the refused widen leaves the working stop unchanged");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("a re-gate refusal transmits nothing");
    }

    // =========================================================================================================
    // gh#278 — move the entry and the working stop together (atomic; full chain re-validated before the venue).
    // =========================================================================================================

    [Fact]
    public async Task CombinedMove_ShouldRepriceEntryAndReStageStop_Atomically()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        // safety 4980 < working 4995 < entry 5008 — the whole chain moves up together.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, workingStopPrice = 4_995m, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the combined move is accepted");
        (await EntryPriceAsync(orderId)).Should().Be(5_008m, "the entry repriced");
        (await WorkingStopAsync(orderId)).Should().Be(4_995m, "the working stop re-staged");
        (StopStaging Staging, decimal ActualStop, decimal EntryPrice)? plan = await StopPlanAsync(orderId);
        plan!.Value.EntryPrice.Should().Be(5_008m, "the plan's entry basis moved with the order");
        plan.Value.ActualStop.Should().Be(4_995m, "the plan's actual stop moved with the order");
        VenueFactory.ModifyOrderCalls.Should().ContainSingle(call =>
            call.VenueOrderKey == VenueKey && call.LimitPrice == 5_008m && call.Size == null,
            "only the entry reaches the venue (at the unchanged size); the working stop is a local re-stage");
    }

    [Fact]
    public async Task CombinedMove_ShouldRefuse422_BeforeTheVenue_WhenGeometryCrosses()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        // The new working stop (4995) would sit ABOVE the new entry (4970) — the chain is broken. Refused BEFORE the
        // venue call, so the entry never reprices against an about-to-fail plan write (the gh#259 desync hazard).
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 4_970m, workingStopPrice = 4_995m, referencePrice = 4_970m });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a crossing combined move is refused");
        (await EntryPriceAsync(orderId)).Should().Be(Entry, "the entry is untouched");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "the working stop is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("nothing reaches the venue when the geometry crosses");
    }

    [Fact]
    public async Task CombinedMove_ShouldLeaveOrderAndPlanUntouched_WhenTheVenueRejectsTheEntry()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);
        VenueFactory.MakeModifyThrow(VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { entryPrice = 5_008m, workingStopPrice = 4_995m, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "the venue rejected the entry modify");
        (await EntryPriceAsync(orderId)).Should().Be(Entry, "a venue rejection leaves the entry untouched (no partial write)");
        (await WorkingStopAsync(orderId)).Should().Be(Working, "and the working stop untouched");
        (StopStaging Staging, decimal ActualStop, decimal EntryPrice)? plan = await StopPlanAsync(orderId);
        plan!.Value.EntryPrice.Should().Be(Entry, "the plan is untouched too — the whole commit is rolled back together");
        plan.Value.ActualStop.Should().Be(Working);
    }

    // =========================================================================================================
    // gh#292 — resize a working order (re-gated at the new size; transmits the gate-approved quantity).
    // =========================================================================================================

    [Fact]
    public async Task Resize_ShouldTransmitTheNewSize_ToTheVenue()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an in-cap resize is accepted");
        (await SizeAsync(orderId)).Should().Be(3, "the order's size updated");
        VenueFactory.ModifyOrderCalls.Should().ContainSingle(call =>
            call.VenueOrderKey == VenueKey && call.Size == 3,
            "a resize MUST transmit the new size to the gateway (unlike a reprice's size == null)");
    }

    [Fact]
    public async Task Resize_ShouldTransmitTheGateApprovedQuantity_WhenGateBindsDown()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        // The per-order cap binds the ask down: 5 asked, 2 approved (MaxContractsPerOrder = 2 is the tightest layer).
        await DeclareRiskProfileAsync(client, accountId, maxContractsPerOrder: 2);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        VenueModifyResponse? body = await response.Content.ReadFromJsonAsync<VenueModifyResponse>(_jsonOptions);
        body!.Size.Should().Be(2, "the venue receives the gate-approved quantity, never the asked one");
        body.RequestedSize.Should().Be(5, "the response echoes what was asked, so a downsize is never silent");
        body.Outcome.Should().Be(nameof(GateOutcome.Resized), "the gate bound the quantity down");
        (await SizeAsync(orderId)).Should().Be(2, "the order persists the gate-approved size");
        VenueFactory.ModifyOrderCalls.Should().ContainSingle(call => call.VenueOrderKey == VenueKey && call.Size == 2,
            "the gate-approved 2 is transmitted, not the asked 5");
    }

    [Fact]
    public async Task Resize_ShouldRefuse422_WhenSizeNotPositive()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "size must be a positive contract count");
        (await SizeAsync(orderId)).Should().Be(1, "the order's size is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Resize_ShouldRefuse400_WhenSizeEqualsCurrent()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        // A lone Size equal to the current size is not a resize — nothing to change.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a no-op same-size request has nothing to change");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Resize_ShouldRefuse409_WhenOrderNotWorking()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey, status: OrderStatus.Filled);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "only a 0-filled working order is resizable");
        (await SizeAsync(orderId)).Should().Be(1, "a filled order's size is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Resize_ShouldRefuse_WhenKillSwitchEngaged()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        // Engage FIRST, then seed the working order: engaging cancels any existing working orders, so a pre-seeded one
        // would be Cancelled and the resize would fail "not working" instead of the outbound-halt we mean to test. A
        // directly-seeded order after engagement is still Working, so the resize reaches ModifyAsync's kill-switch gate.
        await EngageKillSwitchAsync(client);
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price", new { size = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a resize is outbound — refused while the kill switch is engaged");
        RefusalResponse? body = await response.Content.ReadFromJsonAsync<RefusalResponse>(_jsonOptions);
        body!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByKillSwitch));
        (await SizeAsync(orderId)).Should().Be(1, "the order is untouched");
        VenueFactory.ModifyOrderCalls.Should().BeEmpty("nothing reaches the venue while halted");
    }

    [Fact]
    public async Task CombinedResize_ShouldCommitSizeEntryAndStop_Atomically()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, VenueKey);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden);

        // Entry moves up to 5008, so per-contract risk at the safety stop is (5008 - 4980) x $5 = $140; a size of 2
        // fits the $300 per-trade cap (2 x $140 = $280), so the gate approves it as-asked — a clean atomic commit.
        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/orders/{orderId}/price",
            new { entryPrice = 5_008m, workingStopPrice = 4_995m, size = 2, referencePrice = 5_008m });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "size + entry + working-stop move together");
        (await SizeAsync(orderId)).Should().Be(2, "the size changed");
        (await EntryPriceAsync(orderId)).Should().Be(5_008m, "the entry repriced");
        (await WorkingStopAsync(orderId)).Should().Be(4_995m, "the working stop re-staged");
        (await StopPlanAsync(orderId))!.Value.ActualStop.Should().Be(4_995m, "the plan reflects the move");
        VenueFactory.ModifyOrderCalls.Should().ContainSingle(call =>
            call.VenueOrderKey == VenueKey && call.LimitPrice == 5_008m && call.Size == 2,
            "one atomic venue modify carries the new entry and the new size");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        // The kill switch is a process-wide singleton on the shared factory: a prior test that engaged it would leak
        // an engaged state into the next. Reset it so each test starts with outbound enabled.
        _factory.Services.GetRequiredService<KillSwitch>().Disengage();
        await ExecuteDbAsync(async db =>
        {
            await db.GateDecisions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        return await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email, string password)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<HttpClient> SecondOperatorClientAsync()
    {
        string email = $"intruder-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        HttpClient owner = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);

        using HttpResponseMessage invite = await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        invite.EnsureSuccessStatusCode();
        IssueInvitationResponse? issued = await invite.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        using HttpResponseMessage accept = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(issued.Token, password, "Intruder"));
        accept.EnsureSuccessStatusCode();
        return await AuthenticatedClientAsync(email, password);
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Modify-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task DeclareRiskProfileAsync(
        HttpClient client,
        Guid accountId,
        decimal maxDrawdownPerTrade = 300m,
        SizingBasis sizingBasis = SizingBasis.SafetyStop,
        int maxContractsPerOrder = 5)
    {
        DeclareRiskProfileRequest declareReq = new(
            DailyLossLimit: 1_000m,
            AccountProfitTarget: 3_000m,
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
            SizingBasis: sizingBasis,
            MaxContractsPerOrder: maxContractsPerOrder,
            MaxBestDayFraction: 0.4m,
            StartingBalance: 50_000m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> SeedWorkingOrderAsync(
        Guid accountId,
        Guid userId,
        string venueOrderKey,
        OrderStatus status = OrderStatus.Working,
        decimal working = Working,
        decimal safety = Safety,
        int size = 1)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = userId,
                AccountId = accountId,
                Instrument = Contract,
                Symbol = "ES",
                Side = OrderSide.Buy,
                Size = size,
                Type = OrderType.Limit,
                EntryPrice = Entry,
                LimitPrice = Entry,
                WorkingStopPrice = working,
                SafetyStopPrice = safety,
                ReferencePrice = Entry,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = status,
                Mode = TradingMode.Practice,
                VenueOrderKey = venueOrderKey,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private async Task SeedStopPlanAsync(
        Guid orderId, Guid userId, StopStaging staging, decimal actualStop = Working, decimal entry = Entry, decimal safety = Safety)
    {
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = entry,
                ActualStopPrice = actualStop,
                SafetyStopPrice = safety,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
    }

    private Task EngageKillSwitchAsync(HttpClient client) =>
        client.PostAsJsonAsync("/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: "test"));

    private Task<int> SizeAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.Size).SingleAsync());

    private Task<(StopStaging Staging, decimal ActualStop, decimal EntryPrice)?> StopPlanAsync(Guid orderId) => QueryDbAsync(async db =>
    {
        StopPlanRecord? plan = await db.StopPlans.IgnoreQueryFilters().Where(p => p.OrderId == orderId).SingleOrDefaultAsync();
        return plan is null ? ((StopStaging, decimal, decimal)?)null : (plan.Staging, plan.ActualStopPrice, plan.EntryPrice);
    });

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<decimal> EntryPriceAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.EntryPrice).SingleAsync());

    private Task<decimal> WorkingStopAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.WorkingStopPrice).SingleAsync());

    private Task<(AuditAction, string?, string?)> ModifyAuditAsync() => QueryDbAsync(async db =>
    {
        AuditRecord record = await db.AuditRecords.IgnoreQueryFilters()
            .Where(a => a.Action == AuditAction.OrderModified)
            .SingleAsync();
        return (record.Action, record.Before, record.After);
    });

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
