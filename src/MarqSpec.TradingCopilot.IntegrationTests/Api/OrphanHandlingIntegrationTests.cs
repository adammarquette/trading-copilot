using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>connection-loss orphan handling</b> (gh#192, gh#209, R-11, R-19, ADR-0013) against
/// real PostgreSQL. A dropped venue connection must degrade protection <b>visibly and only as far as it should</b>:
/// hidden (synthetic) stops the platform can no longer promote become <c>Orphaned</c> and stop being promotable,
/// while native exchange-held stops — never at risk — are left untouched; on reconnect the orphans re-arm to
/// <c>Hidden</c>, and nothing else silently resumes. Driven through the guard's public
/// <see cref="OrphanGuardService.OrphanAsync"/> / <see cref="OrphanGuardService.RearmAsync"/> and
/// <see cref="StopPromotionService.PromoteForQuoteAsync"/> at a controlled instant, with the adversarial venue stub
/// feeding the venue-truth positions re-arm reconciles against.
/// </summary>
/// <remarks>
/// The guard runs as background plumbing (<c>IgnoreQueryFilters</c>, deployment-wide), so each test clears the
/// stop / order / account / conditional rows and seeds exactly its own fixture. The always-on hosts — above all the
/// 5-second <c>VenueConnectionMonitorHost</c>, which would orphan the seeds itself — are stripped by
/// <see cref="OrphanTestPostgresFactory"/>. The <c>staging &lt;&gt; 0</c> DB check admits <c>Orphaned = 3</c> (which
/// shipped without a migration), proven live by every seed. Traces R-11 · R-19 · ADR-0013. Audit half is gh#220.
/// </remarks>
public class OrphanHandlingIntegrationTests : IClassFixture<OrphanTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    private readonly OrphanTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public OrphanHandlingIntegrationTests(OrphanTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The drop.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Drop_ShouldMoveHiddenStopsToOrphaned_WhenConnectionLost()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        int orphaned = await OrphanAsync();

        orphaned.Should().Be(1, "the one hidden stop is orphaned when the connection drops");
        (await StagingAsync(planId)).Should().Be(StopStaging.Orphaned);
    }

    [Fact]
    public async Task Drop_ShouldLeaveNativeStopsUntouched_WhenConnectionLost()
    {
        // The guard the implementation was red-proven against: a native stop is exchange-held and was never at
        // risk from the connection loss. Orphaning it would misreport real protection as degraded. Seed a hidden
        // one alongside so the sweep is proven to run and touch Hidden — but only Hidden.
        Fixture fixture = await FreshAccountAsync();
        Guid hiddenOrder = await SeedOrderAsync(fixture);
        Guid nativeOrder = await SeedOrderAsync(fixture);
        Guid hiddenPlan = await SeedStopPlanAsync(fixture.OperatorId, hiddenOrder, StopStaging.Hidden);
        Guid nativePlan = await SeedStopPlanAsync(fixture.OperatorId, nativeOrder, StopStaging.Native);

        int orphaned = await OrphanAsync();

        orphaned.Should().Be(1, "only the hidden stop is orphaned — never the native one");
        (await StagingAsync(hiddenPlan)).Should().Be(StopStaging.Orphaned);
        (await StagingAsync(nativePlan)).Should().Be(StopStaging.Native, "a native, exchange-held stop is left untouched");
    }

    [Fact]
    public async Task Drop_ShouldNotOrphanPendingConditional_WhenConnectionLost()
    {
        // A pending conditional is no-risk: nothing rests at the broker, it simply does not fire without quotes.
        // The orphan sweep's blast radius is StopPlans only — a conditional must be left Pending.
        Fixture fixture = await FreshAccountAsync();
        Guid conditionalId = await SeedPendingConditionalAsync(fixture);

        await OrphanAsync();

        (await ConditionalStatusAsync(conditionalId)).Should().Be(
            ConditionalStatus.Pending, "a pending conditional is untouched by a connection drop");
    }

    [Fact]
    public async Task Drop_ShouldLogSyntheticRisk_WhenHiddenStopsOrphaned()
    {
        // The orphaning is an EMERGENCY, not a warning — logged high-severity with the synthetic_risk marker (a log
        // today; the queryable audit row is gh#220).
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        await OrphanAsync();

        _factory.Logs.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("synthetic_risk"),
            "orphaning hidden stops is journalled high-severity with the synthetic_risk marker");
    }

    // ---------------------------------------------------------------------------------------------------------
    // While orphaned.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrphanedStop_ShouldNeverPromote_WhenQuotesResume()
    {
        // By construction: the promotion watcher's query is Hidden-only, so an orphaned stop is never even a
        // candidate. Seed a hidden control on the same contract at the same price so the quote is proven to be one
        // that DOES promote — only the hidden one may.
        Fixture fixture = await FreshAccountAsync();
        Guid hiddenOrder = await SeedOrderAsync(fixture);
        Guid orphanedOrder = await SeedOrderAsync(fixture);
        Guid hiddenPlan = await SeedStopPlanAsync(fixture.OperatorId, hiddenOrder, StopStaging.Hidden);
        Guid orphanedPlan = await SeedStopPlanAsync(fixture.OperatorId, orphanedOrder, StopStaging.Orphaned);

        // Buy stop at 4990, 8-tick band (2.0) → a bid at 4991 is within the band and would promote a hidden stop.
        int promoted = await PromoteAsync(bid: 4_991m, ask: 4_991.25m);

        promoted.Should().Be(1, "only the hidden stop promotes on a promoting quote");
        (await StagingAsync(hiddenPlan)).Should().Be(StopStaging.Native, "the hidden control proves the quote promotes");
        (await StagingAsync(orphanedPlan)).Should().Be(
            StopStaging.Orphaned, "an orphaned stop is never promotable — the platform can no longer promote it");
    }

    [Fact]
    public async Task SafetyStop_ShouldRemainTheFloor_ThroughoutOutage()
    {
        // Degraded means the tighter working stop is lost, never that the position is unprotected: the native
        // safety stop is the floor the whole window. The sweep only moves Staging — the safety price is untouched
        // through orphan and re-arm.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);

        await OrphanAsync();
        (await SafetyAsync(planId)).Should().Be(SafetyStop, "the safety floor stands while orphaned");

        await RearmAsync();
        (await SafetyAsync(planId)).Should().Be(SafetyStop, "and after re-arming — the floor was never touched");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Recovery.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldRearmOrphanedStopsToHidden_WhenConnectionRestored()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1); // position still open at the venue

        await OrphanAsync();
        (await StagingAsync(planId)).Should().Be(StopStaging.Orphaned);

        RearmOutcome outcome = await RearmAsync();

        outcome.Rearmed.Should().Be(1, "the still-open position's stop re-arms on reconnect");
        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden);
    }

    [Fact]
    public async Task Reconnect_ShouldNotAutoResumeAnythingElse_WhenConnectionRestored()
    {
        // Nothing silently resumes (ADR-0013): re-arm touches only Orphaned stops. A native stop, a terminally
        // retired stop, and a pending conditional are all left exactly as they were.
        Fixture fixture = await FreshAccountAsync();
        Guid orphanedOrder = await SeedOrderAsync(fixture);
        Guid nativeOrder = await SeedOrderAsync(fixture);
        Guid retiredOrder = await SeedOrderAsync(fixture);
        Guid orphanedPlan = await SeedStopPlanAsync(fixture.OperatorId, orphanedOrder, StopStaging.Orphaned);
        Guid nativePlan = await SeedStopPlanAsync(fixture.OperatorId, nativeOrder, StopStaging.Native);
        Guid retiredPlan = await SeedStopPlanAsync(fixture.OperatorId, retiredOrder, StopStaging.Retired);
        Guid conditionalId = await SeedPendingConditionalAsync(fixture);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);

        await RearmAsync();

        (await StagingAsync(orphanedPlan)).Should().Be(StopStaging.Hidden, "the orphaned stop re-arms");
        (await StagingAsync(nativePlan)).Should().Be(StopStaging.Native, "a native stop is not resumed by re-arm");
        (await StagingAsync(retiredPlan)).Should().Be(StopStaging.Retired, "a terminally retired stop never re-arms");
        (await ConditionalStatusAsync(conditionalId)).Should().Be(ConditionalStatus.Pending, "a conditional is not resumed");
    }

    [Fact]
    public async Task Restart_ShouldReconcileLingeringOrphans_OnFirstPass()
    {
        // A restart while orphaned stops linger (left by a prior process) must reconcile them, not strand them —
        // the monitor's first pass re-arms when the connection is up. Seed the orphan directly (as if inherited
        // across the restart) and drive the first re-arm pass.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);

        RearmOutcome outcome = await RearmAsync();

        outcome.Rearmed.Should().Be(1, "a lingering orphan is reconciled on the first pass, not left stranded");
        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Boundaries.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orphaning_ShouldPreserveOwnership_WhenGuardBypassesQueryFilter()
    {
        // The guard runs as background plumbing with IgnoreQueryFilters (no request user). Bypassing the R-20
        // filter must never cost ownership: the row's owner is read, never re-assigned.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        await OrphanAsync();

        (await StagingAsync(planId)).Should().Be(StopStaging.Orphaned);
        (await OwnerAsync(planId)).Should().Be(fixture.OperatorId, "the sweep bypasses R-20 but preserves the owner");
    }

    [Fact]
    public async Task Drop_ShouldBeIdempotent_WhenConnectionFlaps()
    {
        // Repeated drop / reconnect cycles converge — no duplicate transitions, no thrash.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);

        (await OrphanAsync()).Should().Be(1, "the first drop orphans the hidden stop");
        (await OrphanAsync()).Should().Be(0, "a second drop finds nothing hidden — no re-orphaning, no thrash");
        (await RearmAsync()).Rearmed.Should().Be(1, "reconnect re-arms it once");
        (await RearmAsync()).Rearmed.Should().Be(0, "a second reconnect finds nothing orphaned");
        (await OrphanAsync()).Should().Be(1, "the flap converges — orphan/re-arm round-trips cleanly");

        (await PlanCountAsync()).Should().Be(1, "flapping never duplicates the plan");
        (await StagingAsync(planId)).Should().Be(StopStaging.Orphaned);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Fixture(Guid AccountId, string VenueKey, Guid OperatorId);

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<Fixture> FreshAccountAsync()
    {
        await ResetStateAsync();
        HttpClient client = await AuthenticatedClientAsync();
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, string venueKey) = await SetupDiscoveredAccountAsync(client, $"Topstep-Orphan-{Guid.NewGuid():N}");
        return new Fixture(accountId, venueKey, operatorId);
    }

    private async Task ResetStateAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.ConditionalOrders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        VenueFactory.ResetPositions();
        _factory.Logs.Clear();
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

    private async Task<(Guid AccountId, string VenueKey)> SetupDiscoveredAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
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

        AccountResponse tradeable = accounts.First(a => a.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task<Guid> SeedOrderAsync(Fixture fixture)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = fixture.OperatorId,
                AccountId = fixture.AccountId,
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
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private async Task<Guid> SeedStopPlanAsync(Guid operatorId, Guid orderId, StopStaging staging)
    {
        Guid planId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = planId,
                UserId = operatorId,
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

    private async Task<Guid> SeedPendingConditionalAsync(Fixture fixture)
    {
        Guid conditionalId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.ConditionalOrders.Add(new ConditionalOrderRecord
            {
                Id = conditionalId,
                UserId = fixture.OperatorId,
                AccountId = fixture.AccountId,
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
                TriggerPrice = 5_010m,
                TriggerDirection = ConditionalCrossDirection.RisesTo,
                Status = ConditionalStatus.Pending,
                Mode = TradingMode.Practice,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return conditionalId;
    }

    private async Task<int> OrphanAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OrphanGuardService guard = scope.ServiceProvider.GetRequiredService<OrphanGuardService>();
        return await guard.OrphanAsync(CancellationToken.None);
    }

    private async Task<RearmOutcome> RearmAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OrphanGuardService guard = scope.ServiceProvider.GetRequiredService<OrphanGuardService>();
        return await guard.RearmAsync(CancellationToken.None);
    }

    private async Task<int> PromoteAsync(decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        StopPromotionService promotion = scope.ServiceProvider.GetRequiredService<StopPromotionService>();
        AdversarialTestProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<AdversarialTestProjectXVenueFactory>();

        // The stub venue ignores conventions on the executor path (fixed roster, no-op place), so None suffices —
        // this call only needs an IOrderExecutor to receive the promoted native stop.
        ITradingVenue venue = venueFactory.Create(FirmConventions.None);

        return await promotion.PromoteForQuoteAsync(VenueId.Parse("projectx"), Contract, bid, ask, venue, CancellationToken.None);
    }

    private Task<StopStaging> StagingAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.Staging).SingleAsync());

    private Task<decimal> SafetyAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.SafetyStopPrice).SingleAsync());

    private Task<Guid> OwnerAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.UserId).SingleAsync());

    private Task<ConditionalStatus> ConditionalStatusAsync(Guid id) => QueryDbAsync(db =>
        db.ConditionalOrders.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.Status).SingleAsync());

    private Task<int> PlanCountAsync() => QueryDbAsync(db => db.StopPlans.IgnoreQueryFilters().CountAsync());

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
