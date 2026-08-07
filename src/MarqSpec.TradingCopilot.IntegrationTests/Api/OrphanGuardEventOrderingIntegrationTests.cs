using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The real orphan-guard commit-then-journal ORDERING guard (gh#715, of gh#707/gh#704). Where
/// <c>OrphanGuardEventLogIntegrationTests</c>'s two consistency cases read <b>after</b> the pass completes — and so
/// cannot tell the two orderings apart, since the terminal state is identical either way — these read <b>during</b>
/// it: <see cref="ObservingEventLog"/> captures the watched <c>StopPlan</c>'s committed staging at the instant
/// <c>protection.orphaned</c> / <c>protection.restored</c> is appended, the <c>ObservingGuard</c> shape (gh#629 /
/// PR #667). A journal-before-commit defect is then directly witnessed: the captured staging is still the
/// PRE-transition value.
/// </summary>
/// <remarks>
/// <b>Not a production defect.</b> <c>OrphanGuardService</c>'s save precedes its journal in both
/// <c>OrphanAsync</c> and <c>RearmAsync</c> today, and stays that way — this suite exists so a future reordering
/// FAILS a test instead of shipping (gh#715's whole point). See the PR description's prove-red section for the
/// reversed-order run that confirms these two cases (and only these) redden.
/// </remarks>
public class OrphanGuardEventOrderingIntegrationTests : IClassFixture<OrphanGuardOrderingPostgresFactory>
{
    private const string Contract = "ESM25";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    private readonly OrphanGuardOrderingPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public OrphanGuardEventOrderingIntegrationTests(OrphanGuardOrderingPostgresFactory factory)
    {
        _factory = factory;
        _factory.Observer.Reset();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The guard itself.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orphan_ShouldAppendTheOrphanedEvent_OnlyAfterTheStopPlanHasCommitted()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        _factory.Observer.PlanId = planId;

        int orphaned = await OrphanAsync();

        orphaned.Should().Be(1, "the seeded hidden stop is the one orphaned");
        _factory.Observer.StagingAtAppend.Should().Be(
            StopStaging.Orphaned,
            "at the instant protection.orphaned is appended, an independent reader must already see the COMMITTED "
            + "Orphaned plan — a journal-before-commit reordering would instead observe the pre-transition Hidden "
            + "value here (gh#715)");
    }

    [Fact]
    public async Task Rearm_ShouldAppendTheRestoredEvent_OnlyAfterTheStopPlanHasCommitted()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1); // still open — re-arms, does not retire
        _factory.Observer.PlanId = planId;

        RearmOutcome outcome = await RearmAsync();

        outcome.Rearmed.Should().Be(1, "the seeded still-open position's stop re-arms");
        _factory.Observer.StagingAtAppend.Should().Be(
            StopStaging.Hidden,
            "at the instant protection.restored is appended, an independent reader must already see the COMMITTED "
            + "re-armed Hidden plan — a journal-before-commit reordering would instead observe the pre-transition "
            + "Orphaned value here (gh#715)");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The guard on the guard (gh#382) — the harness wraps the real log, it does not replace it.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Harness_ShouldWrapTheRealEventLog_NotReplaceIt()
    {
        // The composition root binds IEventLog straight to TimescaleEventLog (no decorator sits there today), so
        // this asserts what THIS harness displaced really was that type — if production ever adds its own
        // decorator, this factory (and this assertion) must be re-pointed at wrapping THAT chain, exactly the
        // discipline InstrumentationSafetyPostgresFactory established for gh#382.
        _factory.DisplacedRegistration.Should().NotBeNull(
            "the composition root must register IEventLog — if it stopped, this harness is reproducing nothing");
        _factory.DisplacedRegistration!.ImplementationType.Should().Be(
            typeof(TimescaleEventLog),
            "production binds IEventLog directly to TimescaleEventLog, which is the concrete type this harness "
            + "keeps alive and wraps — if that binding ever changes, this guard must be re-pointed at the new one "
            + "rather than silently observing an append the deployment does not perform");

        // Resolved from a SCOPE, not from _factory.Services directly. IEventLog is scoped (it holds a scoped
        // DbContext), so asking the root provider for it throws "Cannot resolve scoped service ... from root
        // provider" under the host's scope validation — the assertion never even runs. Resolving the way the
        // production consumers do (OrphanGuardService is itself resolved per scope) is also the only resolve that
        // proves anything about the chain they actually get.
        using IServiceScope scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IEventLog>().Should().BeOfType<ObservingEventLog>(
            "the host these ordering cases actually run against must resolve the decorator, not a bare log");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers — mirrors OrphanGuardEventLogIntegrationTests' fixture shape.
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Fixture(Guid AccountId, string VenueKey, Guid OperatorId);

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<Fixture> FreshAccountAsync()
    {
        await ResetStateAsync();
        HttpClient client = await AuthenticatedClientAsync();
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, string venueKey) = await SetupDiscoveredAccountAsync(client, $"Topstep-OrphanOrdering-{Guid.NewGuid():N}");
        return new Fixture(accountId, venueKey, operatorId);
    }

    private async Task ResetStateAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        VenueFactory.ResetPositions();
        _factory.Observer.Reset();
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

    private async Task<Guid> SeedOrderAsync(Fixture fixture, int size = 1)
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
                Size = size,
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
