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
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>take-profit bracket at entry</b> (gh#170, R-11, ADR-0007): the optional profit
/// leg of the native exchange OCO. The claim under test is that a target is enforced <b>before</b> the gate and, when
/// valid, <b>reaches the venue</b> as the OCO's take-profit side — never flipped. A <b>wrong-side</b> target (below
/// entry for a long, above for a short) is a self-contradiction refused with its own <see cref="ExecutionOutcome"/>
/// code <b>before any sizing</b> — so no order is journaled and no <see cref="GateDecisionRecord"/> is ever written;
/// a <b>winning-side</b> target is accepted and transmitted as <see cref="OrderRequest.ProfitTarget"/> alongside the
/// protective stop (the two-leg bracket); and an order with <b>no</b> target still places (the leg is optional).
/// Driven over HTTP with the adversarial venue stub recording every placed <see cref="OrderRequest"/>; the always-on
/// hosts are stripped by <see cref="OcoExitTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// The wrong-side rule is enforced only in <c>OrderExecutionService.Evaluate</c> (not by a DB constraint), so it
/// needs an execution-path guard: a regression that flipped a wrong-side target instead of refusing it would rest a
/// profit order the next fill turns into an unasked-for position. Assertions read the stub's <b>captured</b>
/// <see cref="OrderRequest.ProfitTarget"/>, never a stub-fabricated echo. Traces R-11 · ADR-0007.
/// </remarks>
public class TakeProfitBracketIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const decimal Entry = 5_000m;

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public TakeProfitBracketIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Send_ShouldRefuseWrongSideTargetBeforeSizing_ForALong()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        // A long profits ABOVE entry, so a target BELOW entry (4995) is on the losing side — a self-contradiction.
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders", LongProposal(target: 4_995m));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a wrong-side target is refused (not a risk block)");
        SendOrderResponse? body = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        body!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByInvalidTarget), "a long's target must sit above entry");
        body.OrderId.Should().BeNull("nothing is journaled for a refused proposal");
        // Refused BEFORE the gate: the wrong-side check precedes sizing, so no gate decision is ever recorded — this
        // distinguishes it from a risk refusal (which sizes to zero and persists a Blocked GateDecisionRecord).
        (await OrderCountAsync()).Should().Be(0, "no order row is written");
        (await GateDecisionCountAsync()).Should().Be(0, "the refusal precedes sizing — no GateDecisionRecord");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "nothing reaches the venue");
    }

    [Fact]
    public async Task Send_ShouldRefuseWrongSideTargetBeforeSizing_ForAShort()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        // A short profits BELOW entry, so a target ABOVE entry (5005) is on the losing side.
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders", ShortProposal(target: 5_005m));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a wrong-side target is refused");
        SendOrderResponse? body = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        body!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByInvalidTarget), "a short's target must sit below entry");
        (await OrderCountAsync()).Should().Be(0);
        (await GateDecisionCountAsync()).Should().Be(0, "the refusal precedes sizing — no GateDecisionRecord");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "nothing reaches the venue");
    }

    [Fact]
    public async Task Send_ShouldAcceptWinningSideTarget_AndTransmitTheProfitLeg()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders", LongProposal(target: 5_010m));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a winning-side target is accepted");
        SendOrderResponse? body = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        body!.Outcome.Should().Be(nameof(ExecutionOutcome.Placed));
        body.OrderId.Should().NotBeNull("the order is journaled");
        // Venue truth: the profit leg was transmitted as the OCO's take-profit side — the value the operator asked
        // for, read from the stub's CAPTURED request (not a fabricated echo). Exactly one new placement this send.
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1, "the accepted order is placed once");
        OrderRequest placed = VenueFactory.AllPlacedOrderRequests[^1];
        placed.ProfitTarget.Should().NotBeNull("the winning-side target reaches the venue as the take-profit leg");
        placed.ProfitTarget!.Value.Value.Should().Be(5_010m, "the transmitted target is exactly what was asked — never flipped");
    }

    [Fact]
    public async Task Send_ShouldTransmitBothLegs_AsTheNativeOco_WhenTargetAndStopGiven()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders", LongProposal(target: 5_010m));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The two-leg native OCO: the protective stop (the always-native safety leg) AND the profit target ride the
        // same placed order, on opposite sides of entry.
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1);
        OrderRequest placed = VenueFactory.AllPlacedOrderRequests[^1];
        placed.ProtectiveStop.Should().NotBeNull("the protective stop is the always-native safety leg");
        placed.ProtectiveStop!.Value.Value.Should().BeLessThan(Entry, "a long's protective stop sits below entry");
        placed.ProfitTarget!.Value.Value.Should().BeGreaterThan(Entry, "a long's profit target sits above entry");
    }

    [Fact]
    public async Task Send_ShouldPlaceWithoutAProfitLeg_WhenNoTargetGiven()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders", LongProposal(target: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the profit leg is optional — an order without one still places");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore + 1);
        VenueFactory.AllPlacedOrderRequests[^1].ProfitTarget.Should().BeNull("no target was asked for, so no profit leg is transmitted");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    // A long: stops below entry (safety 4980 < working 4990 < entry 5000); the target is the variable under test.
    private static SendOrderRequest LongProposal(decimal? target) => new(
        Symbol: "ES", TickSize: 0.25m, PointValue: 5m, Side: OrderSide.Buy, Quantity: 1,
        Entry: Entry, Stop: 4_990m, SafetyStop: 4_980m, ReferencePrice: Entry, Type: OrderType.Market, Target: target);

    // A short: stops above entry (entry 5000 < working 5010 < safety 5020); the target is the variable under test.
    private static SendOrderRequest ShortProposal(decimal? target) => new(
        Symbol: "ES", TickSize: 0.25m, PointValue: 5m, Side: OrderSide.Sell, Quantity: 1,
        Entry: Entry, Stop: 5_010m, SafetyStop: 5_020m, ReferencePrice: Entry, Type: OrderType.Market, Target: target);

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.GateDecisions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Target-{Guid.NewGuid():N}", FirmType.PropFirm));
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

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest declareReq = new(
            DailyLossLimit: 1_000m, AccountProfitTarget: 3_000m, FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday, TrailingAmount: 2_000m, LocksAt: 50_100m, PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m, MaxDrawdownPerTrade: 300m, DailyDrawdownGovernor: 600m, DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true, SizingBasis: SizingBasis.SafetyStop, MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m, StartingBalance: 50_000m);
        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private Task<int> OrderCountAsync() => QueryDbAsync(db => db.Orders.IgnoreQueryFilters().CountAsync());

    private Task<int> GateDecisionCountAsync() => QueryDbAsync(db => db.GateDecisions.IgnoreQueryFilters().CountAsync());

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
