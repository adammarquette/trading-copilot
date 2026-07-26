using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
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
/// Integration coverage for <b>modify-working-order</b> (gh#283 ⇒ gh#259, R-11, R-12, ADR-0007): repricing a resting
/// order's entry in place via <c>PATCH /orders/{id}/price</c>. The claim under test is that a modify is a
/// <b>re-decision, not a rubber stamp</b> — the repriced order is re-gated from scratch at the unchanged size
/// (R-12), the venue is touched <b>only after</b> the gate re-passes, the change is audited (before/after), and a
/// modify that would violate risk or cross its stops is refused rather than sent. Driven over HTTP with the
/// adversarial venue stub recording the in-place modify; the always-on hosts are stripped by
/// <see cref="OcoExitTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// A reprice CAN add risk (unlike a cancel), so it runs the full send ladder and re-gates before the venue call.
/// Working orders are seeded directly (with a known venue handle) — the endpoint needs only the order row + its
/// account/connection + a declared risk profile to resolve the venue and re-decide. Size and the always-native
/// safety bracket are held invariant. The staging bracket-preservation check against a real ProjectX venue is the
/// separate staging follow-up gh#269. Traces R-11 · R-12 · ADR-0007.
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

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
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

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId, decimal maxDrawdownPerTrade = 300m)
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
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m,
            StartingBalance: 50_000m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> SeedWorkingOrderAsync(Guid accountId, Guid userId, string venueOrderKey, OrderStatus status = OrderStatus.Working)
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
                Size = 1,
                Type = OrderType.Limit,
                EntryPrice = Entry,
                LimitPrice = Entry,
                WorkingStopPrice = Working,
                SafetyStopPrice = Safety,
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
