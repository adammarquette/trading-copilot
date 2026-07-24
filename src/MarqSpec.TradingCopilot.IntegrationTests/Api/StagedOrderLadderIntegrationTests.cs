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
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests for the <b>staged ladder</b> — <c>arm → edit → take → cancel</c> — against real PostgreSQL
/// with the applied EF migrations, so the DB-level guards (the gh#96 <c>ct_orders_mode_matches_account</c>
/// constraint trigger, the <c>CK_Orders_*</c> checks) are live (gh#157, gh#11 increment 2, gh#134).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="OrderEndpointsIntegrationTests"/>, which covers the <i>direct</i> send path
/// (<c>POST /accounts/{id}/orders</c>). The ladder is the human-in-the-loop surface: a proposal rests in the
/// database as <see cref="OrderStatus.Staged"/>, is re-gated on every edit, and is re-validated against
/// <b>fresh</b> venue and risk truth at take (R-12) before the venue sees anything.
/// </para>
/// <para>
/// The venue seam is the sole sanctioned double (QA contract). It is adversarial: it reports
/// <see cref="TradingMode.Live"/> for every account regardless of stage, so nothing here can pass by the stub
/// handing back the production-computed answer.
/// </para>
/// </remarks>
public class StagedOrderLadderIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public StagedOrderLadderIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ArmStagedOrder_ShouldPersistStagedOrder_WithoutVenuePlacement()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderArm");
        await DeclareRiskProfileAsync(client, accountId);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/arm", ValidProposal(quantity: 1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        staged.OrderId.Should().NotBeEmpty();
        staged.Status.Should().Be(nameof(OrderStatus.Staged));
        staged.Reason.Should().NotBeNullOrWhiteSpace("R-5 requires the gate's why on every decision");

        // The guard this test exists for: arming evaluates, it does not transmit.
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "arming must never reach the venue — the whole point of staging");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == staged.OrderId);
            order.Status.Should().Be(OrderStatus.Staged);
            order.VenueOrderKey.Should().BeNull("nothing was transmitted, so there is no venue key to record");
            order.AccountId.Should().Be(accountId);
            order.Mode.Should().Be(TradingMode.Practice, "the journaled mode mirrors the account, not the venue's Live claim");

            // The proposal is carried WHOLE on the row — take rebuilds from these exact fields (R-12).
            order.Symbol.Should().Be("MES");
            order.Side.Should().Be(OrderSide.Buy);
            order.Size.Should().Be(1);
            order.EntryPrice.Should().Be(5_000m);
            order.WorkingStopPrice.Should().Be(4_990m);
            order.SafetyStopPrice.Should().Be(4_980m);
            order.ReferencePrice.Should().Be(5_000m);
            order.TickSize.Should().Be(0.25m);
            order.PointValue.Should().Be(5m);

            // A staged order has no stop plan: nothing is at the venue yet, so there is nothing to stage
            // beyond. The plan is recorded when the entry transmits (ADR-0007).
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == staged.OrderId))
                .Should().BeFalse("the staged-stop plan belongs to a transmitted entry, not a staged proposal");
        });
    }

    [Fact]
    public async Task EditStagedOrder_ShouldUpdateProposal_AndAppendGateDecision()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderEdit");
        await DeclareRiskProfileAsync(client, accountId);

        Guid orderId = await ArmAsync(client, accountId, ValidProposal(quantity: 1));

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/orders/{orderId}", ValidProposal(quantity: 2));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Size.Should().Be(2, "the edit carries the new proposal whole");
            order.Status.Should().Be(OrderStatus.Staged, "editing does not move the order off the ladder");

            // The audit trail GROWS — an edit appends a decision, it does not overwrite the arm's.
            List<GateDecisionRecord> decisions = await db.GateDecisions
                .IgnoreQueryFilters()
                .Where(d => d.OrderId == orderId)
                .ToListAsync();
            decisions.Should().HaveCount(2, "arm recorded one decision and the edit appends a second");
        });
    }

    [Fact]
    public async Task TakeStagedOrder_ShouldReValidateFreshState_AndTransmit()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderTake");
        await DeclareRiskProfileAsync(client, accountId);

        Guid orderId = await ArmAsync(client, accountId, ValidProposal(quantity: 1));
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsync($"/orders/{orderId}/take", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SendOrderResponse? taken = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(taken);
        taken.Outcome.Should().Be(nameof(ExecutionOutcome.Placed));
        taken.OrderId.Should().Be(orderId, "taking transmits the SAME ticket — it does not mint a new one");
        taken.VenueOrderKey.Should().NotBeNullOrWhiteSpace();

        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore + 1, "taking transmits exactly once");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Working);
            order.VenueOrderKey.Should().Be(taken.VenueOrderKey);
            order.PlacedAt.Should().BeAfter(default, "the venue's accept timestamp replaces the provisional arm-time value");
        });
    }

    [Fact]
    public async Task TakeStagedOrder_ShouldRefuseWith422_WhenRiskTightenedAfterArming()
    {
        // THE R-12 guard: the arm-time decision is history, not authorization. Taking re-decides from scratch
        // against whatever the limits say NOW — so a profile tightened between arm and take must bind.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderRegate");
        await DeclareRiskProfileAsync(client, accountId);

        // Armed while the proposal passes: $20 of stop distance x $5/point = $100 risk, inside the $300 cap.
        Guid orderId = await ArmAsync(client, accountId, ValidProposal(quantity: 1));

        // Now tighten below that same proposal's risk. The staged row is untouched; only the limits moved.
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 50m);

        int placementsBefore = VenueFactory.TotalPlacedOrderCount;
        using HttpResponseMessage response = await client.PostAsync($"/orders/{orderId}/take", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        SendOrderResponse? refusal = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(refusal);
        refusal.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByRisk));
        refusal.ApprovedQuantity.Should().Be(0);
        refusal.Reason.Should().NotBeNullOrWhiteSpace();

        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "a refused take must not reach the venue");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Staged, "a refused take leaves the ticket on the ladder to be edited");
            order.VenueOrderKey.Should().BeNull();

            (await db.GateDecisions.IgnoreQueryFilters().CountAsync(d => d.OrderId == orderId))
                .Should().Be(2, "the refusal is auditable — arm's decision plus the take's refusal");
        });
    }

    [Fact]
    public async Task CancelStagedOrder_ShouldLeaveStaging_WithoutVenueContact()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderCancel");
        await DeclareRiskProfileAsync(client, accountId);

        Guid orderId = await ArmAsync(client, accountId, ValidProposal(quantity: 1));
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.DeleteAsync($"/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "nothing was ever at the venue, so cancelling cannot touch it");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);

            // Cancel is a SOFT terminal transition — the row survives as history rather than being deleted,
            // which keeps the gate decisions that reference it meaningful.
            order.Status.Should().Be(OrderStatus.Cancelled);
            order.VenueOrderKey.Should().BeNull();
        });
    }

    [Fact]
    public async Task TakeStagedOrder_ShouldRefuse_AndNotDoubleTransmit_WhenTheOrderHasAlreadyBeenTaken()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderDoubleTake");
        await DeclareRiskProfileAsync(client, accountId);

        Guid orderId = await ArmAsync(client, accountId, ValidProposal(quantity: 1));

        using HttpResponseMessage first = await client.PostAsync($"/orders/{orderId}/take", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        int placementsAfterFirst = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage second = await client.PostAsync($"/orders/{orderId}/take", null);

        // gh#157 spec said 404; the shipped shape is 409 Conflict, which is the better answer — the order
        // plainly exists, it has simply left staging. Asserting the real contract; the refusal, not its code,
        // is the safety property. (Noted on the PR so the issue's test list can be corrected.)
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsAfterFirst, "the ladder is not re-entrant — a second take must never double-transmit");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Working, "the first take's result stands");
        });
    }

    [Fact]
    public async Task TakeStagedOrder_ShouldSizeAgainstTheWorkingStop_WhenALimitOrderCarriesNoVenueTrigger()
    {
        // The gh#134 regression guard. A Limit order has NO venue trigger, so `Order.StopPrice` is null; the
        // protective stop lives in `WorkingStopPrice`. Rebuilding the take from the trigger column instead
        // would size against a stop of zero — catastrophic risk per contract — and the take would be refused
        // rather than placed. Reaching Placed is therefore the evidence that the working stop was used.
        //
        // SizingBasis.ActualStop is LOAD-BEARING here, and is the condition gh#134 names. Under the suite's
        // default SafetyStop basis the gate sizes off SafetyStopPrice, so WorkingStopPrice never reaches the
        // arithmetic and this test cannot fail on the swap — verified: with the defect injected it still
        // passed. Only ActualStop puts the working stop on the path this guards.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderWorkingStop");
        await DeclareRiskProfileAsync(client, accountId, sizingBasis: SizingBasis.ActualStop);

        SendOrderRequest limitProposal = ValidProposal(quantity: 1) with { Type = OrderType.Limit };
        Guid orderId = await ArmAsync(client, accountId, limitProposal);

        await ExecuteDbContextAsync(async db =>
        {
            Order staged = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            staged.StopPrice.Should().BeNull("a Limit order carries no venue trigger");
            staged.LimitPrice.Should().Be(5_000m);
            staged.WorkingStopPrice.Should().Be(4_990m, "the protective stop is kept for every order type");
        });

        using HttpResponseMessage response = await client.PostAsync($"/orders/{orderId}/take", null);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "sizing must use WorkingStopPrice; rebuilding from the null venue trigger would refuse this order");

        SendOrderResponse? taken = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(taken);
        taken.Outcome.Should().Be(nameof(ExecutionOutcome.Placed));
        taken.ApprovedQuantity.Should().Be(1, "$20 of working-stop distance x $5/point = $100, inside the $300 cap");

        // The entry reached the venue as a Limit carrying its always-native protective stop (ADR-0007).
        OrderRequest transmitted = VenueFactory.AllPlacedOrderRequests[^1];
        transmitted.Type.Should().Be(OrderType.Limit);
        transmitted.LimitPrice.Should().NotBeNull();
        transmitted.ProtectiveStop.Should().NotBeNull("an entry a venue cannot protect must never transmit");
    }

    [Fact]
    public async Task StagedOrder_ShouldBeRejectedByTheDatabase_WhenItsModeDivergesFromItsAccount()
    {
        // The gh#96 cross-table guard (`ct_orders_mode_matches_account`). A single-row CHECK cannot express it,
        // so it is a constraint trigger in the applied migration — invisible to any in-memory provider. Written
        // straight through the DbContext because the point is that the DATABASE refuses what the API never
        // emits: nothing above it is the last line here.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderModeGuard");

        await ExecuteDbContextAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            account.Mode.Should().Be(TradingMode.Practice);

            db.Orders.Add(NewRawOrder(account, TradingMode.Live));

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.MessageText.Contains("R-14 mode guard", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task StagedOrder_ShouldBeRejectedByTheDatabase_WhenItsModeIsUndeclared()
    {
        // The fail-closed companion: `CK_Orders_Mode_NotUndeclared`. Undeclared is not a mode, it is the
        // absence of one, and absence is refusal rather than a default (R-14).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-LadderModeUndeclared");

        await ExecuteDbContextAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);

            db.Orders.Add(NewRawOrder(account, TradingMode.Undeclared));

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    || error.MessageText.Contains("R-14 mode guard", StringComparison.Ordinal));
        });
    }

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private static Order NewRawOrder(Account account, TradingMode mode) => new()
    {
        Id = Guid.NewGuid(),
        UserId = account.UserId,
        AccountId = account.Id,
        Instrument = "MESM25",
        Symbol = "MES",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        Status = OrderStatus.Staged,
        Mode = mode,
        EntryPrice = 5_000m,
        WorkingStopPrice = 4_990m,
        SafetyStopPrice = 4_980m,
        ReferencePrice = 5_000m,
        TickSize = 0.25m,
        PointValue = 5m,
        PlacedAt = DateTimeOffset.UtcNow,
    };

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must arm cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
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

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered account.</summary>
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

    private async Task DeclareRiskProfileAsync(
        HttpClient client,
        Guid accountId,
        decimal maxDrawdownPerTrade = 300m,
        SizingBasis sizingBasis = SizingBasis.SafetyStop)
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
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m,
            StartingBalance: 50_000m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private static SendOrderRequest ValidProposal(int quantity = 1) => new(
        Symbol: "MES",
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
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
