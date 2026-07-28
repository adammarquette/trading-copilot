using System.Globalization;
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
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// The execution SLIs (gh#330 ⇒ gh#232 / gh#295, ADR-0002, engineering §7, PRD §7) against real PostgreSQL: not
/// that the instruments <i>exist</i>, but that what they report is <b>true</b> — the gate counter agrees with the
/// rows it claims to mirror, a quiet deadline is distinguishable from a missed one, the two flatten tiers are never
/// merged, the gauges track live state across a restart, and no unbounded value ever becomes a label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the acceptance criteria</b> of gh#232 / gh#295 plus ADR-0002 and engineering §7 — each case
/// asserts the <i>invariant the spec promises</i> rather than the mechanism that happens to implement it, so a
/// re-wiring that still satisfies the spec keeps these green and one that quietly stops satisfying it does not.
/// </para>
/// <para>
/// <b>Independence disclosure.</b> This author did not write the shipped SLI implementation (gh#316), but has read
/// its call sites while delivering gh#331 and gh#343, so the independence this tier normally carries is
/// <b>reduced</b>. The mitigation is the one above — assert invariants, never mechanisms — and a reviewer should
/// weight these guards accordingly.
/// </para>
/// <para>
/// The sink is swapped for one on a private meter (see <see cref="ExecutionSliTestPostgresFactory"/>), so a
/// listener sees only this fixture's measurements. Hosted services are stripped, so every flatten pass is the
/// test's own explicit one.
/// </para>
/// </remarks>
public class ExecutionSliIntegrationTests : IClassFixture<ExecutionSliTestPostgresFactory>
{
    /// <summary>The dimensions ADR-0002 declares a closed set. Anything else is unbounded-label risk.</summary>
    private static string[] AllowedTagKeys { get; } = ["outcome", "binding_layer", "tier", "consumer_group"];

    // Summer date → CDT (UTC−5): ES closes 14:30 CT = 19:30 UTC. Mirrors the flatten suites.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private readonly ExecutionSliTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ExecutionSliIntegrationTests(ExecutionSliTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GateMetric_ShouldReconcileWithGateDecisionRows_WhenOrdersAreEvaluated()
    {
        // PRD §7's "100% risk-gate coverage" is only observable if the counter and the journal AGREE. A drift means
        // one of the two is lying, and the counter is the one a dashboard trusts. Asserted as a reconciliation of
        // totals and outcomes, not against any particular emission site — a re-wiring that still reconciles passes.
        Capture.Clear();
        await ClearGateDecisionsAsync();
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await TradeableAccountAsync(client);

        await SendAsync(client, accountId, quantity: 1);   // within every layer -> Allowed
        await SendAsync(client, accountId, quantity: 5);   // beyond the per-trade cap -> Resized or Blocked
        await SendAsync(client, accountId, quantity: 50);  // far beyond -> Blocked

        IReadOnlyList<SliCapture.Measurement> counted = Capture.For(ExecutionMetrics.GateDecisions);
        List<string> journalled = await GateDecisionOutcomesAsync();

        // Reconciled on the counter's TOTAL, not the number of measurements: a single Add(2) would leave the
        // measurement count right and the counter wrong, which is precisely the drift a dashboard would believe.
        counted.Sum(measurement => measurement.Value).Should().Be(
            journalled.Count,
            "every persisted gate decision is counted exactly once — the counter's total and the rows must reconcile");
        counted.Should().HaveCount(
            journalled.Count, "and as one measurement each, so no decision is counted in a batch with another");
        counted.Select(measurement => measurement.Tags["outcome"]).OrderBy(outcome => outcome, StringComparer.Ordinal)
            .Should().Equal(
                journalled.OrderBy(outcome => outcome, StringComparer.Ordinal),
                "and each counted outcome matches the outcome that was journalled");
        counted.Should().OnlyContain(
            measurement => measurement.Tags.ContainsKey("binding_layer"),
            "the binding layer is always present — a tag that appears only sometimes breaks aggregation");
    }

    [Fact]
    public async Task FlattenMetric_ShouldSignalAbsence_WhenTheDeadlinePassesWithNothingToFlatten()
    {
        // THE ONE MOST LIKELY TO BE GOT WRONG (gh#232's own words). A counter that merely stays at zero cannot be
        // told apart from a healthy quiet day, so the failure this system exists to prevent would look exactly like
        // an ordinary Tuesday. The deadline must emit whatever the outcome.
        Capture.Clear();
        await FreshAccountKeyAsync(); // an account, deliberately with NO open position

        await RunFlattenPassAsync(Utc(19, 45)); // past the 14:30 CT ES deadline

        Capture.For(ExecutionMetrics.FlattenDeadlines).Should().NotBeEmpty(
            "a deadline that passes with nothing to flatten must still emit — silence is indistinguishable from a "
            + "flatten that never fired");
    }

    [Fact]
    public async Task FlattenMetric_ShouldNeverMergeTheWatchdogWithThePrimary_WhenBothRun()
    {
        // The whole point of a redundant second tier is knowing WHICH one saved you. Merged counters would hide a
        // primary quietly failing every day while the watchdog silently covered for it.
        Capture.Clear();
        string account = await FreshAccountKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        await RunFlattenPassAsync(Utc(19, 45));

        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        await RunWatchdogPassAsync(Utc(19, 45));

        IReadOnlyList<SliCapture.Measurement> deadlines = Capture.For(ExecutionMetrics.FlattenDeadlines);
        deadlines.Should().OnlyContain(
            measurement => measurement.Tags.ContainsKey("tier"), "every flatten signal names the tier that produced it");
        deadlines.Select(measurement => measurement.Tags["tier"]).Distinct(StringComparer.Ordinal)
            .Should().HaveCountGreaterThan(1, "the primary and the watchdog are reported as different tiers, never merged");
    }

    [Fact]
    public async Task OrphanMetric_ShouldRiseOnTheDrop_AndReturnToZeroOnRearm()
    {
        // The gauge is the live synthetic-risk exposure an operator watches during an outage. It must both RISE
        // (or the drop is invisible) and RETURN TO ZERO (or it cries wolf forever after one bad afternoon).
        Capture.Clear();
        string account = await FreshAccountKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        await SeedHiddenStopAsync(account);

        await OrphanAsync();

        // Asserted against the DATABASE first, so a failure names the right culprit: if nothing orphaned, the
        // fixture is wrong; if rows orphaned but the gauge stayed flat, the SLI is.
        int orphanedRows = await QueryDbAsync(db => db.StopPlans.IgnoreQueryFilters()
            .CountAsync(plan => plan.Staging == StopStaging.Orphaned));
        orphanedRows.Should().BeGreaterThan(0, "the drop must actually have orphaned the seeded hidden stop");

        Capture.PumpGauges();
        LatestGauge(ExecutionMetrics.OrphanedStops)
            .Should().Be(orphanedRows, "the gauge must report the live synthetic-risk exposure the rows show");

        await RearmAsync();
        Capture.PumpGauges();
        LatestGauge(ExecutionMetrics.OrphanedStops)
            .Should().Be(0, "and the gauge returns to zero once the last orphan clears");
    }

    [Fact]
    public async Task Metric_ShouldBoundCardinality_AcrossManyAccountsAndOrders()
    {
        // An unbounded label set takes the metrics backend down — instrumentation causing the very outage it exists
        // to reveal (§9). Asserted BY CONSTRUCTION over everything the suite has emitted: the tag keys are a closed
        // set, and no tag VALUE is an id, however many accounts and orders pass through.
        HttpClient client = await AuthenticatedClientAsync();
        for (int account = 0; account < 3; account++)
        {
            Guid accountId = await TradeableAccountAsync(client);
            await SendAsync(client, accountId, quantity: 1);
            await SendAsync(client, accountId, quantity: 50);
        }

        IReadOnlyList<SliCapture.Measurement> all = Capture.All;
        all.Should().NotBeEmpty("the sweep must actually have measured something");

        all.SelectMany(measurement => measurement.Tags.Keys).Distinct(StringComparer.Ordinal)
            .Should().BeSubsetOf(AllowedTagKeys, "dimensions come from a closed set — anything else is unbounded");
        all.SelectMany(measurement => measurement.Tags.Values)
            .Should().OnlyContain(
                value => value == null || !IsIdentifier(value),
                "no account id, order id, or other unbounded value may ever become a label");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Whether a tag value looks like an id — the unbounded-cardinality shape a label must never carry.</summary>
    private static bool IsIdentifier(string value) => Guid.TryParse(value, out Guid _);

    /// <summary>
    /// The newest reading of a gauge <b>from this host's meter</b>. Attribution is required, not tidiness: the
    /// restart case publishes a second meter carrying the same instruments, so an unfiltered "last value" can be
    /// the other host's — which is how a gauge that was correct read as zero.
    /// </summary>
    /// <param name="instrument">The gauge's instrument name.</param>
    /// <returns>Its latest value on this fixture's primary meter.</returns>
    private double LatestGauge(string instrument) => Capture.For(instrument)
        .Where(measurement => string.Equals(measurement.MeterName, _factory.MeterName, StringComparison.Ordinal))
        .Select(measurement => measurement.Value)
        .Last();

    private SliCapture Capture => _factory.Capture;

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

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

    /// <summary>Firm → conventions → connection → discovered account, with a declared risk profile so sends are gated.</summary>
    private async Task<Guid> TradeableAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Sli-{Guid.NewGuid():N}", FirmType.PropFirm));
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
        Guid accountId = accounts.First(account => account.CanTrade).Id;

        using HttpResponseMessage risk = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", RiskProfile());
        risk.EnsureSuccessStatusCode();
        return accountId;
    }

    private static DeclareRiskProfileRequest RiskProfile() => new(
        DailyLossLimit: 1_000m,
        AccountProfitTarget: 3_000m,
        StartingBalance: 50_000m,
        FloorSource: FloorSource.FirmImposed,
        TrailingMode: TrailingMode.Intraday,
        TrailingAmount: 2_000m,
        LocksAt: 50_100m,
        PerTradeRiskFraction: 0.15m,
        TargetRewardRatio: 1.5m,
        MaxDrawdownPerTrade: 300m,
        DailyDrawdownGovernor: 600m,
        DailyProfitTarget: 1_500m,
        StopForDayAtProfitTarget: true,
        SizingBasis: SizingBasis.SafetyStop,
        MaxContractsPerOrder: 3,
        MaxBestDayFraction: 0.4m);

    private async Task SendAsync(HttpClient client, Guid accountId, int quantity)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/",
            new SendOrderRequest(
                Symbol: "MES",
                TickSize: 0.25m,
                PointValue: 5m,
                Side: OrderSide.Buy,
                Quantity: quantity,
                Entry: 5_000m,
                Stop: 4_990m,
                SafetyStop: 4_980m,
                ReferencePrice: 5_000m,
                Type: OrderType.Market));

        // Whatever the gate decided is fine — this suite asserts on what was COUNTED versus what was JOURNALLED,
        // never on the verdict itself.
        _ = response;
    }

    /// <summary>A fresh account (accounts cleared first), returning its venue key — the flatten path's identifier.</summary>
    private async Task<string> FreshAccountKeyAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync());
        await ExecuteDbAsync(db => db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync());
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await TradeableAccountAsync(client);
        return await QueryDbAsync(db => db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == accountId)
            .Select(account => account.VenueAccountKey)
            .SingleAsync());
    }

    /// <summary>A working order with a hidden stop plan on <paramref name="accountKey"/> — what a drop orphans.</summary>
    private async Task SeedHiddenStopAsync(string accountKey)
    {
        Guid operatorId = await QueryDbAsync(db => db.Users
            .Where(user => user.Email == PostgresApiFactory.OperatorEmail)
            .Select(user => user.Id)
            .FirstAsync());
        Guid accountId = await QueryDbAsync(db => db.Accounts.IgnoreQueryFilters()
            .Where(account => account.VenueAccountKey == accountKey)
            .Select(account => account.Id)
            .FirstAsync());

        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = "CON.F.US.EP.U26",
                Symbol = "ES",
                Side = OrderSide.Buy,
                Size = 2,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                EntryPrice = 5_300m,
                WorkingStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ReferencePrice = 5_300m,
                TickSize = 0.25m,
                PointValue = 5m,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_300m,
                ActualStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = StopStaging.Hidden,
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task RunFlattenPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    private async Task RunWatchdogPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenWatchdogService service = scope.ServiceProvider.GetRequiredService<AutoFlattenWatchdogService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    private async Task OrphanAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OrphanGuardService guard = scope.ServiceProvider.GetRequiredService<OrphanGuardService>();
        await guard.OrphanAsync(CancellationToken.None);
    }

    private async Task RearmAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OrphanGuardService guard = scope.ServiceProvider.GetRequiredService<OrphanGuardService>();
        await guard.RearmAsync(CancellationToken.None);
    }

    private Task ClearGateDecisionsAsync() =>
        ExecuteDbAsync(db => db.GateDecisions.IgnoreQueryFilters().ExecuteDeleteAsync());

    private Task<List<string>> GateDecisionOutcomesAsync() => QueryDbAsync(db => db.GateDecisions
        .IgnoreQueryFilters()
        .Select(decision => decision.Outcome)
        .ToListAsync()
        .ContinueWith(
            outcomes => outcomes.Result.Select(outcome => outcome.ToString()).ToList(),
            TaskScheduler.Default));

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}

/// <summary>
/// The kill-switch gauge across a <b>restart</b> (gh#330 ⇒ gh#232): the durable row already survives one (gh#189);
/// the metric must too, or a dashboard shows a killed system as healthy for as long as the process has been up —
/// and a restart is exactly when an operator looks.
/// </summary>
/// <remarks>
/// Deliberately its <b>own class</b>, so xUnit gives it its own fixture — its own database and its own meters. The
/// case boots a second host, and a second host publishing the same instruments into a shared listener is precisely
/// the cross-talk that made a correct gauge read as zero when this shared a fixture with the other SLI cases.
/// </remarks>
public class ExecutionSliRestartIntegrationTests : IClassFixture<ExecutionSliTestPostgresFactory>
{
    private readonly ExecutionSliTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ExecutionSliRestartIntegrationTests(ExecutionSliTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task KillSwitchMetric_ShouldReadEngaged_AfterARestartWithTheDurableRowEngaged()
    {
        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage engaged = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: "sli probe"));
        engaged.EnsureSuccessStatusCode();

        // A genuine restart: a second host on the same database with a FRESH sink, whose gauges start at zero — so
        // only rehydration can move them. Reusing the first host's sink would read the pre-restart value and prove
        // nothing.
        using WebApplicationFactory<Program> restarted = _factory.CreateRestartedHost();
        using HttpClient rebooted = restarted.CreateClient(); // building the host re-runs the startup tasks
        _factory.Capture.PumpGauges();

        IReadOnlyList<SliCapture.Measurement> onTheRestartedHost =
        [
            .. _factory.Capture.For(ExecutionMetrics.KillSwitchEngaged)
                .Where(measurement => string.Equals(
                    measurement.MeterName, _factory.RestartedMeterName, StringComparison.Ordinal)),
        ];

        onTheRestartedHost.Should().NotBeEmpty("the restarted host must publish the gauge at all");
        onTheRestartedHost[^1].Value.Should().Be(
            1, "a restart with the durable lock engaged must come up reading engaged, not healthy");
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
}
