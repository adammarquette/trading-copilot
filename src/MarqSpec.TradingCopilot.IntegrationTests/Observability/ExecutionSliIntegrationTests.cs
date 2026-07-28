using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Pre-merge coverage for the <b>execution SLIs</b> (gh#330 ⇒ gh#232/#295, ADR-0002, engineering §7), asserted
/// through an in-process <see cref="MetricsCapture"/> (<c>MeterListener</c>) double against <b>real Postgres</b> — so
/// the gate / orphan / kill-switch rows the SLIs claim to mirror are the real ones. Written independently of the
/// instrumentation (QA contract): the venue only feeds inputs; the listener asserts what the pipeline received.
/// </summary>
/// <remarks>
/// The claim under test: the SLIs describe what actually happened — a counter that drifts from its rows, or a
/// silence indistinguishable from health, is worse than no signal. Isolation is by construction: each host publishes
/// on a unique meter name (<see cref="MetricsCapturingPostgresFactory"/>), so a parallel class never contaminates
/// this listener. Traces ADR-0002 · §7 · §9.
/// </remarks>
public sealed class ExecutionSliIntegrationTests : IClassFixture<MetricsCapturingPostgresFactory>
{
    private const string VenueKey = "PRAC-50K-101";
    private const string Contract = "ESM25";

    private readonly MetricsCapturingPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ExecutionSliIntegrationTests(MetricsCapturingPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GateMetric_ShouldReconcileWithGateDecisionRows_WhenOrdersEvaluated()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        using MetricsCapture capture = new(_factory.MeterName);

        const int sends = 3;
        for (int i = 0; i < sends; i++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        int measured = capture.For(ExecutionMetrics.GateDecisions).Count;
        int rows = await GateDecisionRowCountAsync();

        measured.Should().Be(sends, "one gate-decision measurement per evaluated order");
        rows.Should().Be(sends, "each allowed send persists exactly one GateDecisionRecord");
        // The counter emits at EVALUATION (OrderExecutionService), the row at persistence — so the counter may lead
        // but must never TRAIL the rows (a trailing counter under-reports real decisions). gh#330 flags the reverse
        // drift (a rolled-back save over-counts); pin it here if ever observed rather than blessing it.
        measured.Should().BeGreaterThanOrEqualTo(rows, "the SLI never under-reports the decisions the journal recorded");
    }

    [Fact]
    public async Task FlattenMetric_ShouldDistinguishWatchdogFromPrimary_WhenBothEvaluate()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);
        using MetricsCapture capture = new(_factory.MeterName);

        // Primary evaluates its deadline with nothing open -> a primary-tier signal.
        await RunPrimaryFlattenAsync(Utc(19, 45));
        // The watchdog evaluates a still-open position past the deadline -> a watchdog-tier signal.
        VenueFactory.SeedPosition(VenueKey, Contract, netQuantity: 1);
        await RunWatchdogFlattenAsync(Utc(19, 45));

        IReadOnlyList<string> tiers = capture.TagValues(ExecutionMetrics.FlattenDeadlines, "tier");
        tiers.Should().Contain("primary").And.Contain("watchdog",
            "the two flatten tiers are distinguished — merging them would hide a primary failing daily while the watchdog covers");
    }

    [Fact]
    public async Task FlattenMetric_ShouldSignalAbsence_WhenDeadlinePassesWithoutFiring()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);
        using MetricsCapture capture = new(_factory.MeterName);

        // A deadline passes with nothing open. It must still EMIT (nothing-to-do), or "never fired" and "nothing to
        // do" are the same silence — the failure this system exists to prevent looking like an ordinary Tuesday.
        await RunPrimaryFlattenAsync(Utc(19, 45));

        capture.For(ExecutionMetrics.FlattenDeadlines)
            .Where(measurement => (measurement.Tags.GetValueOrDefault("outcome")?.ToString()) == ExecutionMetrics.FlattenNothingToDo)
            .Should().NotBeEmpty("an idle deadline emits a nothing-to-do signal — absence is observable, not inferred");
    }

    [Fact]
    public async Task OrphanMetric_ShouldRiseOnDropAndReturnToZeroOnRearm()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, "WORK-ORPHAN");
        await SeedStopPlanAsync(operatorId, orderId, StopStaging.Hidden);
        VenueFactory.SeedPosition(VenueKey, Contract, netQuantity: 1); // open, so re-arm re-validates to Hidden (not Retired)

        int orphaned = await OrphanAsync();
        orphaned.Should().Be(1, "precondition: the one seeded hidden stop is orphaned on the drop");
        // Max over a fresh scrape: this host's live gauge is what changed; a disposed/other host from the restart
        // test lingering on the shared meter only ever reads 0, so the max is this host's value either way.
        ScrapeGaugeMax(ExecutionMetrics.OrphanedStops).Should().Be(1, "the gauge rises to the live orphaned count on a drop");

        await RearmAsync();
        ScrapeGaugeMax(ExecutionMetrics.OrphanedStops).Should().Be(0, "re-arm returns the gauge to zero — re-counted, never a stale delta");
    }

    [Fact]
    public async Task KillSwitchMetric_ShouldRemainObservable_WhenHostRestartsWhileEngaged()
    {
        HttpClient client = await FreshOperatorAsync();
        using MetricsCapture capture = new(_factory.MeterName);

        // Engage on the live host: the durable row is written AND this host's gauge goes to 1.
        using HttpResponseMessage engage = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, "gh#330 restart-observability probe"));
        engage.StatusCode.Should().Be(HttpStatusCode.OK);

        // Restart: a fresh host rehydrates the durable engaged row at startup. Its gauge must come up at 1 too — a
        // durable lock the dashboard cannot see is a lock that fails silently.
        await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
        _ = restarted.Server; // force the host (and its startup rehydration) to build

        capture.Scrape();
        IReadOnlyList<double> engagedGauge = [.. capture.For(ExecutionMetrics.KillSwitchEngaged).Select(measurement => measurement.Value)];
        engagedGauge.Should().NotBeEmpty("the kill-switch gauge is scraped from both the live and the restarted host");
        engagedGauge.Should().AllSatisfy(value => value.Should().Be(1,
            "every engaged host reports 1 — a restarted host that rehydrated the lock but not the gauge would surface a 0"));
    }

    [Fact]
    public async Task Metric_ShouldBoundCardinality_WhenManyAccountsAndOrders()
    {
        HttpClient client = await FreshOperatorAsync();
        using MetricsCapture capture = new(_factory.MeterName);

        // Two accounts, several orders each: an unbounded label (account id / order id) would make the tag-key set
        // grow with them and take the metrics backend down — instrumentation causing the outage it exists to reveal.
        foreach (int account in new[] { 0, 1 })
        {
            Guid accountId = await SetupAccountAsync(client, account);
            await DeclareRiskProfileAsync(client, accountId);
            for (int i = 0; i < 2; i++)
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }

        capture.For(ExecutionMetrics.GateDecisions).Should().HaveCountGreaterThan(2, "several orders across accounts were evaluated");
        capture.TagKeys(ExecutionMetrics.GateDecisions).Should().BeEquivalentTo(
            ["outcome", "binding_layer"],
            "the gate SLI's label set is closed — no account id, order id, or other unbounded dimension, however many orders flow");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 27, hour, minute, 0, TimeSpan.Zero);

    /// <summary>One fresh scrape of the gauges, returning the highest value seen for <paramref name="instrument"/>
    /// (0 if none) — robust to another host lingering on the shared meter, which only ever reads 0.</summary>
    private double ScrapeGaugeMax(string instrument)
    {
        using MetricsCapture capture = new(_factory.MeterName);
        capture.Scrape();
        return capture.For(instrument).Select(measurement => measurement.Value).DefaultIfEmpty(0).Max();
    }

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        // The kill switch is a process-wide singleton: the restart test engages it, and clearing only the durable
        // row would leave the in-memory lock on, refusing every later send. Disengage the runtime mirror too.
        _factory.Services.GetRequiredService<KillSwitch>().Disengage();
        await ExecuteDbAsync(async db =>
        {
            await db.GateDecisions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.KillSwitchStates.ExecuteDeleteAsync();
        });
        return await AuthenticatedClientAsync();
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private static string VenueKeyFor(int slot) => slot == 0 ? VenueKey : "50KTC-V2-202";

    private async Task<Guid> SetupAccountAsync(HttpClient client, int slot = 0)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Sli-{slot}-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        // Declare BOTH no-capital-at-risk stages so PRAC-50K-101 (Practice) and 50KTC-V2-202 (Evaluation) both
        // resolve to a tradeable Practice mode — the two distinct accounts the cardinality test drives orders on.
        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest(
            [
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.First(candidate => candidate.VenueAccountKey == VenueKeyFor(slot));
        account.CanTrade.Should().BeTrue($"the seeded account {VenueKeyFor(slot)} must be tradeable for the SLI send path");
        return account.Id;
    }

    private Task DeclareRiskProfileAsync(HttpClient client, Guid accountId) =>
        client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 1_000m,
                AccountProfitTarget: 3_000m,
                FloorSource: MarqSpec.TradingCopilot.Domain.Risk.FloorSource.FirmImposed,
                TrailingMode: MarqSpec.TradingCopilot.Domain.Risk.TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: 50_100m,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 300m,
                DailyDrawdownGovernor: 600m,
                DailyProfitTarget: 1_500m,
                StopForDayAtProfitTarget: true,
                SizingBasis: MarqSpec.TradingCopilot.Domain.Risk.SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: 0.4m,
                StartingBalance: 50_000m));

    private static SendOrderRequest ValidProposal() => new(
        Symbol: "MES",
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: 1,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);

    private async Task RunPrimaryFlattenAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AutoFlattenService>().RunPassAsync(now, CancellationToken.None);
    }

    private async Task RunWatchdogFlattenAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AutoFlattenWatchdogService>().RunPassAsync(now, CancellationToken.None);
    }

    private async Task<int> OrphanAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrphanGuardService>().OrphanAsync(CancellationToken.None);
    }

    private async Task RearmAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrphanGuardService>().RearmAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedWorkingOrderAsync(Guid accountId, Guid userId, string venueOrderKey)
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
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
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

    private async Task SeedStopPlanAsync(Guid userId, Guid orderId, StopStaging staging)
    {
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_000m,
                ActualStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
    }

    private Task<int> GateDecisionRowCountAsync() =>
        QueryDbAsync(db => db.GateDecisions.IgnoreQueryFilters().CountAsync());

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

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
