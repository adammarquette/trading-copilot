using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests for the <b>send-as-is fast path</b> (gh#181) and the <b>default entry action preference</b>
/// (gh#218), against real PostgreSQL with the applied migrations, so the DB-level guards are live (gh#182).
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim under test is that the fast path is a shortcut past the GATE.</b> It is not — it is a shortcut
/// past the <i>manual review</i> only. Every assertion here exists to prove the enforcement floor is identical on
/// both paths: R-5 sizing, R-16 caps, the R-14 mode × environment gate, the always-native protective stop, and the
/// kill switch all bind exactly as they do on <c>arm → take</c>.
/// </para>
/// <para>
/// Written from R-11 / ADR-0007 and the issue's acceptance criteria rather than from the implementation (QA
/// contract). The venue seam is the sole sanctioned double and is adversarial: it reports
/// <see cref="TradingMode.Live"/> for every account regardless of stage, so nothing here passes by the stub
/// handing back the production-computed answer.
/// </para>
/// </remarks>
public class SendAsIsEndpointsIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public SendAsIsEndpointsIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
        VenueFactory.ResetPositions();
    }

    // -------------------------------------------------------------------------------------------------------
    // The gate holds on the fast path (gh#181)
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsIs_ShouldResizeToApprovedQuantity_WhenTicketBreachesRiskLayer()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Topstep-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);

        int before = VenueFactory.TotalPlacedOrderCount;

        // 5 contracts against a 300 max-DD-per-trade and a 10-point safety stop cannot all be authorized.
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal(quantity: 5));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SendOrderResponse? sent = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        sent.Should().NotBeNull();
        sent!.ApprovedQuantity.Should().BeLessThan(5, "the gate must bind on the fast path exactly as on take");

        // The load-bearing assertion: the VENUE received the approved size, not the requested one. Transmitting
        // what was asked for after a resize would make the gate advisory -- the precise failure this path invites.
        VenueFactory.TotalPlacedOrderCount.Should().Be(before + 1);
        VenueFactory.AllPlacedOrderRequests[^1].Quantity.Should().Be(sent.ApprovedQuantity);
    }

    [Fact]
    public async Task SendAsIs_ShouldReachVenueIdenticallyToArmedTake_WhenTicketIsEquivalent()
    {
        // THE CENTREPIECE. Two paths, one ticket, one enforcement floor: whatever the operator skipped by going
        // fast, it was not a guard. A divergence here is the whole risk of the feature.
        HttpClient client = await AuthenticatedClientAsync();

        Guid fastAccount = await SetupTradeableAccountAsync(client, $"Fast-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, fastAccount);
        using HttpResponseMessage fast = await client.PostAsJsonAsync(
            $"/accounts/{fastAccount}/orders/send-as-is", ValidProposal(quantity: 3));
        fast.StatusCode.Should().Be(HttpStatusCode.OK);
        SendOrderResponse? fastSent = await fast.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        OrderRequest fastRequest = VenueFactory.AllPlacedOrderRequests[^1];

        Guid ladderAccount = await SetupTradeableAccountAsync(client, $"Ladder-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, ladderAccount);
        Guid staged = await ArmAsync(client, ladderAccount, ValidProposal(quantity: 3));
        using HttpResponseMessage taken = await client.PostAsync($"/orders/{staged}/take", null);
        taken.StatusCode.Should().Be(HttpStatusCode.OK);
        SendOrderResponse? takenSent = await taken.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        OrderRequest ladderRequest = VenueFactory.AllPlacedOrderRequests[^1];

        fastSent!.ApprovedQuantity.Should().Be(takenSent!.ApprovedQuantity, "the same ticket must size identically");
        fastRequest.Quantity.Should().Be(ladderRequest.Quantity);
        fastRequest.Side.Should().Be(ladderRequest.Side);
        fastRequest.Type.Should().Be(ladderRequest.Type);

        // The protective legs are the point: a fast entry must arrive at the venue wearing the same armour.
        fastRequest.ProtectiveStop.Should().Be(ladderRequest.ProtectiveStop);
        fastRequest.ProfitTarget.Should().Be(ladderRequest.ProfitTarget);
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenKillSwitchEngaged()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Kill-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);

        using HttpResponseMessage engage = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, "qa gh#182"));
        engage.StatusCode.Should().Be(HttpStatusCode.OK);

        try
        {
            int before = VenueFactory.TotalPlacedOrderCount;

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/accounts/{accountId}/orders/send-as-is", ValidProposal());

            // 409, not 422: the send path distinguishes a PRE-GATE refusal (nothing sized, no decision to report)
            // from a gate refusal (sized and blocked, 422 carrying the decision). The kill switch is the former by
            // design — it refuses before the ticket is even sized.
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            VenueFactory.TotalPlacedOrderCount.Should().Be(before, "nothing may reach the venue while engaged");

            // Refused BEFORE sizing: the lock is not a risk decision, so it leaves no gate decision behind.
            await ExecuteDbContextAsync(async db =>
                (await db.GateDecisions.IgnoreQueryFilters().CountAsync(d => d.AccountId == accountId)).Should().Be(0));
        }
        finally
        {
            using HttpResponseMessage disengage = await client.PostAsync("/kill-switch/disengage", null);
            disengage.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse422_WhenAccountHasNoRiskProfile()
    {
        // Absence is refusal, never a default. A fast path that invented limits would be the worst of both.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"NoRisk-{Guid.NewGuid():N}");

        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        VenueFactory.TotalPlacedOrderCount.Should().Be(before);
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenAccountModeIsLiveOutsideProduction()
    {
        // R-14: the test host is not production, so a LIVE account is untradeable here. Mode is recomputed from
        // the operator's declared conventions -- capital at risk ⇒ Live -- never taken from the venue's flag.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(
            client, $"LiveFirm-{Guid.NewGuid():N}", capitalAtRisk: true);
        await DeclareRiskProfileAsync(client, accountId);

        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal());

        // A pre-gate refusal (409): the mode × environment verdict precedes sizing, so there is no decision to
        // carry — matching the direct path's documented shape (OrderEndpointsIntegrationTests, gh#130).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        VenueFactory.TotalPlacedOrderCount.Should().Be(before);
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenVenueCannotHoldProtectiveStop()
    {
        // Better no trade than an unprotected one (ADR-0007). The fast path must fail CLOSED on a venue that
        // cannot hold an exchange-side stop -- the one refusal an impatient operator would most want skipped.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"NoBracket-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);

        VenueFactory.MakeBracketsUnsupported();
        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal());

        // Pre-gate again (409): an entry the venue cannot protect is refused before sizing.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        VenueFactory.TotalPlacedOrderCount.Should().Be(before, "an entry that cannot be protected is not sent");
    }

    [Fact]
    public async Task SendAsIs_ShouldPersistGateDecision_WhenBlocked()
    {
        // Every SIZED attempt is auditable, placed or blocked. A fast path that skipped the audit trail would
        // erase exactly the evidence a post-mortem needs.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Audit-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 1m);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal(quantity: 5));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await ExecuteDbContextAsync(async db =>
        {
            List<GateDecisionRecord> decisions = await db.GateDecisions.IgnoreQueryFilters()
                .Where(decision => decision.AccountId == accountId)
                .ToListAsync();

            decisions.Should().ContainSingle();
            decisions[0].Outcome.Should().Be(GateOutcome.Blocked);
            decisions[0].OrderId.Should().BeNull("a blocked attempt has no order to link");
        });
    }

    [Fact]
    public async Task SendAsIs_ShouldLeaveNoStagedOrderRow_WhenRefused()
    {
        // The fast path never stages. A refusal must not leave an orphan the operator could later "take".
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"NoOrphan-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId, maxDrawdownPerTrade: 1m);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal(quantity: 5));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await ExecuteDbContextAsync(async db =>
            (await db.Orders.IgnoreQueryFilters().CountAsync(order => order.AccountId == accountId)).Should().Be(0));
    }

    // -------------------------------------------------------------------------------------------------------
    // The preference and its guards (gh#218)
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultEntryAction_ShouldResolveToApproveAndArm_WhenNeverSet()
    {
        // Review-first is the shipped default; making the fast path primary is an opt-in, never an inheritance.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"DefaultPref-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        RiskProfileResponse? profile = await response.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);

        profile!.DefaultEntryAction.Should().Be(DefaultEntryAction.ApproveAndArm);
    }

    [Fact]
    public async Task DefaultEntryAction_ShouldRefuseSendAsIs_WhenAccountModeIsLive()
    {
        // Practice-only, enforced SERVER-SIDE. Hiding the option in the UI is not a guard -- a crafted request
        // would sail past it, on the account where the mistake costs real money.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(
            client, $"LivePref-{Guid.NewGuid():N}", capitalAtRisk: true);

        using HttpResponseMessage response = await DeclareRiskAsync(
            client, accountId, DefaultEntryAction.SendAsIs, confirm: true);

        // 409 rather than 422: this is a conflict with the ACCOUNT STATE (a live account), not a malformed
        // request. The missing-confirmation case below is the 422 -- both refuse, and the distinction is honest.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DefaultEntryAction_ShouldRefuse422_WhenEnablingWithoutConfirmation()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"NoConfirm-{Guid.NewGuid():N}");

        using HttpResponseMessage response = await DeclareRiskAsync(
            client, accountId, DefaultEntryAction.SendAsIs, confirm: false);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DefaultEntryAction_ShouldPersistToTheDatabase_WhenEnabledOnPracticeWithConfirmation()
    {
        // Asserted at the ROW, not the response: the persisted value is what a restart rehydrates from, so that
        // is what "survives a restart" actually means here.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Persist-{Guid.NewGuid():N}");

        using HttpResponseMessage response = await DeclareRiskAsync(
            client, accountId, DefaultEntryAction.SendAsIs, confirm: true);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExecuteDbContextAsync(async db =>
        {
            RiskProfileRecord row = await db.RiskProfiles.IgnoreQueryFilters().SingleAsync(p => p.AccountId == accountId);
            row.DefaultEntryAction.Should().Be(DefaultEntryAction.SendAsIs);
        });
    }

    [Fact]
    public async Task DefaultEntryAction_ShouldNotWeakenGate_WhenSetToSendAsIs()
    {
        // The preference changes which button is primary and NOTHING else. If setting it moved a limit, the
        // feature would be a risk control wearing a UX label.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"NoWeaken-{Guid.NewGuid():N}");

        using HttpResponseMessage declared = await DeclareRiskAsync(
            client, accountId, DefaultEntryAction.SendAsIs, confirm: true, maxDrawdownPerTrade: 1m);
        declared.StatusCode.Should().Be(HttpStatusCode.OK);

        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/send-as-is", ValidProposal(quantity: 5));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the gate binds regardless of the preference");
        VenueFactory.TotalPlacedOrderCount.Should().Be(before);
    }

    [Fact]
    public async Task DefaultEntryAction_ShouldBeInvisibleToAnotherOperator_WhenQueried()
    {
        // R-20 default-deny at the data layer: another operator's context must see NOTHING, not a shared setting.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Isolated-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();

        await using TradingCopilotDbContext asStranger = new(options, new FixedUser(Guid.NewGuid()));

        (await asStranger.RiskProfiles.AnyAsync(profile => profile.AccountId == accountId))
            .Should().BeFalse("a query that forgets its scope must return nothing, not everything");
    }

    // -------------------------------------------------------------------------------------------------------
    // Helpers — mirroring the gh#157 staged-ladder harness rather than standing up a second fixture.
    // -------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

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

    /// <summary>Firm → conventions → connection → discovered account. Capital at risk ⇒ the account resolves Live.</summary>
    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName, bool capitalAtRisk = false)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, capitalAtRisk),
                new StageConventionDto(AccountStage.Evaluation, capitalAtRisk),
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
        using HttpResponseMessage response = await DeclareRiskAsync(
            client, accountId, DefaultEntryAction.ApproveAndArm, confirm: false, maxDrawdownPerTrade);
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> DeclareRiskAsync(
        HttpClient client,
        Guid accountId,
        DefaultEntryAction entryAction,
        bool confirm,
        decimal maxDrawdownPerTrade = 300m) =>
        client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
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
                MaxBestDayFraction: 0.4m,
                DefaultEntryAction: entryAction,
                ConfirmSendAsIsDefault: confirm));

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
