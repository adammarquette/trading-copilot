using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>OCO-cancel-on-exit</b> (gh#184, gh#183, R-11, ADR-0007): a position's protection
/// comes down when — and <b>only</b> when — the position is actually gone. The account-event seam's flat
/// <c>PositionEvent</c> (gh#219) is the single funnel for every exit route (manual flatten, promoted actual stop,
/// auto-flatten, kill-switch flatten-all), so one flat event drives <c>OcoExitService.ProcessExitAsync</c>: it
/// retires the hidden / native / orphaned stop plan (→ <c>Retired</c>), cancels the dangling native legs at the
/// venue (sparing the operator's journaled entry), and audits each retirement (<c>AuditAction.PositionExit</c>).
/// The dangerous direction is guarded hardest: a <b>partial fill never retires protection</b>.
/// </summary>
/// <remarks>
/// The stub cannot push account events (no <c>AccountStreaming</c> capability), so the suite drives
/// <c>ProcessExitAsync</c> directly at a chosen <c>PositionEvent</c> — the deterministic pattern the orphan suite
/// uses. Empty venue positions ⇒ the re-confirm-flat guard passes; seeding an open position exercises the re-open
/// race. Native-leg cancellation uses the QA-owned stub extensions (`SeedWorkingOrder` / recorded
/// `CancelOrderAsync`). The service resolves ownership across owners then works in a per-owner context, so R-20
/// holds. Always-on hosts suppressed via <see cref="OcoExitTestPostgresFactory"/>. Traces R-11 · ADR-0007.
/// </remarks>
public class OcoCancelOnExitIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const string VenueKey = "PRAC-50K-101";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public OcoCancelOnExitIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Protection comes down.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exit_ShouldRetireHiddenStopPlan_AndAudit_WhenPositionFlat()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        await ProcessExitAsync(netQuantity: 0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Retired, "a hidden stop plan is retired when its position exits");
        AuditRecord audit = await SingleAuditForAsync(planId);
        audit.Action.Should().Be(AuditAction.PositionExit);
        audit.After.Should().Be(nameof(StopStaging.Retired));
        audit.Placement.Should().Be(AuditPlacement.Synthetic, "a hidden plan was synthetic");
        audit.SyntheticRisk.Should().BeFalse("a clean exit is not a synthetic-risk event");
        audit.UserId.Should().Be(fixture.OperatorId);
    }

    [Fact]
    public async Task Exit_ShouldRetireOrphanedStopPlan_WhenPositionFlat()
    {
        // An orphaned plan is still a plan — it must not survive its position.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);

        await ProcessExitAsync(netQuantity: 0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Retired, "an orphaned stop plan is retired on exit, never left behind");
    }

    [Fact]
    public async Task Exit_ShouldCancelNativeLeg_AndAuditNative_WhenPositionFlat()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Native);
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract); // the native safety leg resting at the venue

        await ProcessExitAsync(netQuantity: 0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Retired);
        VenueFactory.CancelOrderCalls.Should().Contain(call => call.VenueOrderKey == "STOP-1", "the dangling native leg is cancelled");
        (await SingleAuditForAsync(planId)).Placement.Should().Be(AuditPlacement.Native, "a promoted plan's retirement is audited as native");
    }

    [Fact]
    public async Task Exit_ShouldSpareTheJournaledEntry_WhenCancellingLegs()
    {
        // Cancel the protective legs — never the operator's own journaled entry order. Cancelling too much strands
        // the operator; cancelling the entry could leave a resting order that opens a position nobody asked for.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId, venueOrderKey: "ENTRY-1");
        await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Native);
        VenueFactory.SeedWorkingOrder(VenueKey, "ENTRY-1", Contract); // the operator's journaled entry, still resting
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract);  // a dangling protective leg

        await ProcessExitAsync(netQuantity: 0);

        VenueFactory.CancelOrderCalls.Should().Contain(call => call.VenueOrderKey == "STOP-1", "the dangling leg is cancelled");
        VenueFactory.CancelOrderCalls.Should().NotContain(call => call.VenueOrderKey == "ENTRY-1", "the operator's journaled entry is spared");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Protection stays up — the dangerous direction.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exit_ShouldNotCancelProtection_WhenFillIsPartial()
    {
        // THE highest-value guard: a partial fill leaves the net non-zero — the remaining position is still live and
        // MUST stay protected. Only a flat (net==0) position retires protection.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract);

        await ProcessExitAsync(netQuantity: 1); // a partial — still 1 lot long

        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden, "a partial fill must not retire protection — the remaining position is live");
        (await AuditCountAsync()).Should().Be(0, "nothing was retired, so nothing is audited");
        VenueFactory.CancelOrderCalls.Should().BeEmpty("no protective leg is cancelled on a partial fill");
    }

    [Fact]
    public async Task Exit_ShouldNotRetireProtection_WhenVenueStillShowsPositionOpen()
    {
        // A stale/duplicate flat event must not retire protection while the venue still shows exposure (a re-open
        // race). The reconcile re-confirms flatness against venue truth before touching anything.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedPosition(VenueKey, Contract, netQuantity: 1); // venue still reports the position open

        await ProcessExitAsync(netQuantity: 0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden, "the venue still shows the position open — protection stays up");
    }

    [Fact]
    public async Task Exit_ShouldNotTouchAnotherOperatorsPlan_WhenPositionExits()
    {
        // R-20: the retire runs in the resolved owner's context, so another user's plan on the same account is
        // invisible and untouched.
        Fixture fixture = await FreshAccountAsync();
        Guid ownerOrder = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid ownerPlan = await SeedStopPlanAsync(fixture.OperatorId, ownerOrder, StopStaging.Hidden);

        Guid stranger = Guid.NewGuid();
        Guid strangerOrder = await SeedOrderAsync(fixture.AccountId, stranger); // a foreign order on the same account
        Guid strangerPlan = await SeedStopPlanAsync(stranger, strangerOrder, StopStaging.Hidden);

        await ProcessExitAsync(netQuantity: 0);

        (await StagingAsync(ownerPlan)).Should().Be(StopStaging.Retired, "the resolved owner's plan is retired");
        (await StagingAsync(strangerPlan)).Should().Be(StopStaging.Hidden, "another operator's plan is invisible to the per-owner retire (R-20)");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Robustness.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exit_ShouldBeNoOp_WhenExitEventIsReplayed()
    {
        // Idempotent by construction: the retire selects only Hidden/Native/Orphaned, so a replayed exit finds the
        // plan already Retired and does nothing more — no second audit row.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        await ProcessExitAsync(netQuantity: 0);
        await ProcessExitAsync(netQuantity: 0); // replay

        (await StagingAsync(planId)).Should().Be(StopStaging.Retired);
        (await AuditCountAsync()).Should().Be(1, "a replayed exit is a no-op — exactly one retirement is audited");
    }

    [Fact]
    public async Task Exit_ShouldNotCorruptRecord_WhenVenueRejectsCancelAsAlreadyGone()
    {
        // A venue cancel rejected as "already gone" is benign: one attempt, warning-logged, never retried. The
        // retire (the safety action, committed first) stands, and no exception escapes.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture.AccountId, fixture.OperatorId);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Native);
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract);
        VenueFactory.MakeCancelThrow("STOP-1"); // the venue rejects the cancel — the leg was already gone

        Func<Task> exit = () => ProcessExitAsync(netQuantity: 0);

        await exit.Should().NotThrowAsync("a rejected cancel is swallowed — it must not corrupt the retire or escape");
        (await StagingAsync(planId)).Should().Be(StopStaging.Retired, "the retire stands regardless of the leg cancel outcome");
        VenueFactory.CancelOrderCalls.Count(call => call.VenueOrderKey == "STOP-1").Should().Be(1, "one attempt per leg — never a retry-storm");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Fixture(Guid AccountId, Guid OperatorId);

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<Fixture> FreshAccountAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SetupAccountAsync(client);
        return new Fixture(accountId, operatorId);
    }

    private async Task ProcessExitAsync(int netQuantity)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OcoExitService oco = scope.ServiceProvider.GetRequiredService<OcoExitService>();
        PositionEvent exit = new(
            VenueAccountId.Create(VenueId.Parse("projectx"), VenueKey),
            DateTimeOffset.UtcNow,
            VenueContractId.Create(VenueId.Parse("projectx"), Contract),
            netQuantity,
            new Price(5_300m));
        await oco.ProcessExitAsync(exit, CancellationToken.None);
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

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Oco-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        return accounts.First(a => a.VenueAccountKey == VenueKey).Id;
    }

    private async Task<Guid> SeedOrderAsync(Guid accountId, Guid userId, string? venueOrderKey = null)
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
                Type = OrderType.Market,
                EntryPrice = Entry,
                WorkingStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ReferencePrice = Entry,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                VenueOrderKey = venueOrderKey,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private async Task<Guid> SeedStopPlanAsync(Guid userId, Guid orderId, StopStaging staging)
    {
        Guid planId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = planId,
                UserId = userId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = Entry,
                ActualStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
        return planId;
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<StopStaging> StagingAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.Staging).SingleAsync());

    private Task<int> AuditCountAsync() => QueryDbAsync(db => db.AuditRecords.IgnoreQueryFilters().CountAsync());

    private Task<AuditRecord> SingleAuditForAsync(Guid planId) => QueryDbAsync(db =>
        db.AuditRecords.IgnoreQueryFilters().Where(a => a.StopPlanId == planId).SingleAsync());

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
