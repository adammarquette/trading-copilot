using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the orphan-guard <b>event-log journalling</b> shipped by gh#704
/// (<c>OrphanGuardService</c> emits <c>protection.orphaned</c> / <c>protection.restored</c> on the event log) —
/// authored from the spec (gh#707, gh#704, R-11 / R-19, ADR-0013), not the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a container-tier suite.</b> <c>OrphanGuardServiceTests</c> (unit tier) proves the emission logic
/// against a <b>faked</b> <c>IEventLog</c> — it can show the guard <i>calls</i> <c>AppendAsync</c> with the right
/// arguments, but nothing about what a real append does. This suite proves, against the applied migrations on
/// <c>timescale/timescaledb-ha:pg17</c>: a genuine append with a log-assigned sequence, that a subsequent
/// <see cref="IEventLog.ReadAfterAsync"/> returns it with the count and the R-19 flag intact in the payload; that
/// <c>protection.restored</c> is appended for the transitioning re-arm shapes (re-arm, retire, partial-close) and
/// withheld on an all-unverifiable pass; that the event is appended only <b>after</b> the <c>StopPlan</c>
/// transition has durably committed; and — the safety-critical half — that a genuine Postgres-level append
/// failure leaves the orphaning / re-arm <b>committed</b>, never rolled back.
/// </para>
/// <para>
/// <b>The append-fault tests use a real Postgres fault, not a mock.</b> The QA contract's sole sanctioned test
/// double is the venue seam; journalling failures are instead reproduced with a genuine, temporary
/// <c>CHECK (false) NOT VALID</c> constraint added to the live <c>"Events"</c> table (dropped in a
/// <c>finally</c>), so every append made during the test window fails with a real PostgreSQL check-violation —
/// the same shape a production outage on the event-log store would produce. <c>NOT VALID</c> means existing rows
/// are never scanned/validated (so a shared container's prior test data is untouched); only new inserts made
/// while the constraint stands are rejected.
/// </para>
/// <para>
/// Reuses <see cref="OrphanTestPostgresFactory"/> (gh#192/#209/#191) — every always-on host is stripped so
/// <c>OrphanAsync</c> / <c>RearmAsync</c> run only at the instant this suite calls them, and the adversarial venue
/// stub is the sole test double for re-arm's venue-truth re-validation. Each test seeds its own account/order/plan
/// and reads events strictly <b>after</b> a captured baseline sequence, since the shared container's
/// <c>"Events"</c> table is never wiped between tests (its rows are the log's own append-only history — deleting
/// them would falsify the very thing under test). Traces R-11 · R-19 · ADR-0013 · gh#704 · gh#209/#191/#220.
/// </para>
/// </remarks>
public class OrphanGuardEventLogIntegrationTests : IClassFixture<OrphanTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    /// <summary>Name of the temporary real-Postgres constraint the append-fault tests install and remove.</summary>
    private const string BlockAppendConstraint = "ck_test_block_event_append";

    private readonly OrphanTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public OrphanGuardEventLogIntegrationTests(OrphanTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Append + readability — the orphan pass.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orphan_ShouldAppendAReadableProtectionOrphanedEvent_CarryingTheCountAndTheR19Flag()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid firstOrder = await SeedOrderAsync(fixture);
        Guid secondOrder = await SeedOrderAsync(fixture);
        await SeedStopPlanAsync(fixture.OperatorId, firstOrder, StopStaging.Hidden);
        await SeedStopPlanAsync(fixture.OperatorId, secondOrder, StopStaging.Hidden);
        long baseline = await LatestEventSequenceAsync();

        int orphaned = await OrphanAsync();

        orphaned.Should().Be(2, "both hidden stops are orphaned on the connection loss");

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        EventEnvelope envelope = events.Single(e => e.Type == OrphanGuardService.OrphanedEventType);

        envelope.Sequence.Should().BePositive("a real append is assigned a log sequence, not a placeholder");
        envelope.Source.Should().Be(OrphanGuardService.EventSource);

        using JsonDocument payload = JsonDocument.Parse(envelope.Payload);
        payload.RootElement.GetProperty("orphaned").GetInt32().Should().Be(
            2, "the payload carries the count actually orphaned, read back from a real append/read round trip");
        payload.RootElement.GetProperty("nativeSafetyStopRemainsFloor").GetBoolean().Should().BeTrue(
            "R-19: the operator's tighter working stop is orphaned, but the native safety stop remains the "
            + "floor — this must never read as \"unprotected\"");
    }

    [Fact]
    public async Task Orphan_ShouldAppendNothing_WhenNoHiddenStopsAreOrphaned()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Native);
        long baseline = await LatestEventSequenceAsync();

        int orphaned = await OrphanAsync();

        orphaned.Should().Be(0, "a native stop was never at risk from the connection loss");

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        events.Should().NotContain(
            e => e.Type == OrphanGuardService.OrphanedEventType,
            "no protection changed, so a consumer reading the log must see nothing new to alert on");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Append + readability — re-arm, and its transitioning variants (re-arm, retire, partial close).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rearm_ShouldAppendAProtectionRestoredEvent_WhenAStopReArms()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1); // still open at the venue
        long baseline = await LatestEventSequenceAsync();

        RearmOutcome outcome = await RearmAsync();

        outcome.Rearmed.Should().Be(1, "the still-open position's stop re-arms on reconnect");

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        EventEnvelope envelope = events.Single(e => e.Type == OrphanGuardService.RestoredEventType);
        envelope.Sequence.Should().BePositive();
        envelope.Source.Should().Be(OrphanGuardService.EventSource);

        using JsonDocument payload = JsonDocument.Parse(envelope.Payload);
        payload.RootElement.GetProperty("rearmed").GetInt32().Should().Be(1);
        payload.RootElement.GetProperty("retired").GetInt32().Should().Be(0);
        payload.RootElement.GetProperty("stillOrphaned").GetInt32().Should().Be(0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden);
    }

    [Fact]
    public async Task Rearm_ShouldAppendAProtectionRestoredEvent_WhenAStopRetiresBecauseThePositionClosedDuringTheOutage()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        // Deliberately NO position seeded on this contract: the venue reports it flat, so the stop retires
        // rather than re-arming (gh#191) — and the transition still journals a restored event.
        long baseline = await LatestEventSequenceAsync();

        RearmOutcome outcome = await RearmAsync();

        outcome.Retired.Should().Be(1, "the protected position closed during the outage, so the stop is retired, never re-armed");
        outcome.Rearmed.Should().Be(0);

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        EventEnvelope envelope = events.Single(e => e.Type == OrphanGuardService.RestoredEventType);

        using JsonDocument payload = JsonDocument.Parse(envelope.Payload);
        payload.RootElement.GetProperty("retired").GetInt32().Should().Be(
            1, "a retirement is still a transition the operator's alert must clear on, so it is journalled too");
        payload.RootElement.GetProperty("rearmed").GetInt32().Should().Be(0);

        (await StagingAsync(planId)).Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task Rearm_ShouldAppendAProtectionRestoredEvent_OnAPartialCloseThatStillReArmed()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture, size: 3);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        // The venue now reports only 1 lot open of the original 3 — a partial close during the outage. Re-arm
        // reconciles the protected quantity first (gh#191), then still re-arms and journals.
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);
        long baseline = await LatestEventSequenceAsync();

        RearmOutcome outcome = await RearmAsync();

        outcome.Rearmed.Should().Be(1, "a partial close reconciles the size but still re-arms — it does not retire");

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        EventEnvelope envelope = events.Single(e => e.Type == OrphanGuardService.RestoredEventType);
        using JsonDocument payload = JsonDocument.Parse(envelope.Payload);
        payload.RootElement.GetProperty("rearmed").GetInt32().Should().Be(1);

        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden);
        (await OrderSizeAsync(orderId)).Should().Be(
            1, "the reconciled size protects what actually remains, not the original 3 lots — and the restored "
               + "event fires for this reconciled re-arm too");
    }

    [Fact]
    public async Task Rearm_ShouldAppendNoProtectionRestoredEvent_WhenReValidationCannotReachTheVenue()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1); // would re-arm IF the venue were reachable
        VenueFactory.MakeVenueUnreachable();
        long baseline = await LatestEventSequenceAsync();

        RearmOutcome outcome = await RearmAsync();

        outcome.StillOrphaned.Should().Be(1, "an unreachable venue cannot be re-validated against, so nothing re-arms on an unverified assumption");
        outcome.Rearmed.Should().Be(0);
        outcome.Retired.Should().Be(0);

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        events.Should().NotContain(
            e => e.Type == OrphanGuardService.RestoredEventType,
            "an all-unverifiable pass restores nothing, so nothing is journalled — the earlier orphaned alert must stand");

        (await StagingAsync(planId)).Should().Be(StopStaging.Orphaned, "left orphaned — never re-armed on nothing");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Ordering — the event is visible only after the safety transition is durable.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orphan_ShouldAppendTheEventOnlyAfterTheStopPlanHasCommitted()
    {
        // Reads the StopPlan back from a BRAND-NEW scope after observing the event — a fresh DbContext is a
        // fresh connection with no shared change tracker, so this is what any independent consumer reacting to
        // protection.orphaned would itself see over Postgres, not merely what the acting scope remembers doing.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);
        long baseline = await LatestEventSequenceAsync();

        await OrphanAsync();

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        events.Should().Contain(e => e.Type == OrphanGuardService.OrphanedEventType);

        (await StagingAsync(planId)).Should().Be(
            StopStaging.Orphaned,
            "the event is appended only after the StopPlan orphan has committed — a consumer that reacts to "
            + "protection.orphaned by reading the StopPlan must never observe it still Hidden");
    }

    [Fact]
    public async Task Rearm_ShouldAppendTheEventOnlyAfterTheStopPlanHasCommitted()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);
        long baseline = await LatestEventSequenceAsync();

        await RearmAsync();

        IReadOnlyList<EventEnvelope> events = await EventsAfterAsync(baseline);
        events.Should().Contain(e => e.Type == OrphanGuardService.RestoredEventType);

        (await StagingAsync(planId)).Should().Be(
            StopStaging.Hidden,
            "the restored event is appended only after the re-arm has committed — a consumer must never observe "
            + "protection.restored for a stop still recorded Orphaned");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The best-effort contract — a REAL Postgres append failure, not a mock.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orphan_ShouldStillOrphanTheStops_WhenTheEventLogAppendFails()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        await BlockEventAppendsAsync();
        try
        {
            int orphaned = await OrphanAsync();

            orphaned.Should().Be(1, "a failing secondary write must not stop the guard from reporting what it orphaned");
            (await StagingAsync(planId)).Should().Be(
                StopStaging.Orphaned,
                "a REAL event-log append failure must never roll back or undo the already-committed safety action");

            _factory.Logs.Entries.Should().Contain(
                entry => entry.Level == LogLevel.Error && entry.Message.Contains("synthetic_risk"),
                "the high-severity operator alert still stands even though the secondary event-log write failed");
        }
        finally
        {
            await UnblockEventAppendsAsync();
        }
    }

    [Fact]
    public async Task Rearm_ShouldStillReArmTheStops_WhenTheEventLogAppendFails()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedOrderAsync(fixture);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Orphaned);
        VenueFactory.SeedPosition(fixture.VenueKey, Contract, netQuantity: 1);

        await BlockEventAppendsAsync();
        try
        {
            RearmOutcome outcome = await RearmAsync();

            outcome.Rearmed.Should().Be(1, "a failing secondary write must not stop the guard from re-arming a verified-open position");
            (await StagingAsync(planId)).Should().Be(
                StopStaging.Hidden,
                "a REAL event-log append failure must never roll back or undo the already-committed re-arm");
        }
        finally
        {
            await UnblockEventAppendsAsync();
        }
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
        (Guid accountId, string venueKey) = await SetupDiscoveredAccountAsync(client, $"Topstep-OrphanEventLog-{Guid.NewGuid():N}");
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

    /// <summary>The highest sequence appended so far — the baseline a test reads strictly after, since the
    /// shared container's <c>"Events"</c> table is never wiped between tests in this class.</summary>
    private Task<long> LatestEventSequenceAsync() => QueryDbAsync(async db =>
        await db.Events.AsNoTracking().Select(e => (long?)e.Sequence).MaxAsync() ?? 0L);

    private async Task<IReadOnlyList<EventEnvelope>> EventsAfterAsync(long afterSequence, int limit = 50)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return (await log.ReadAfterAsync(afterSequence, limit, CancellationToken.None)).Events;
    }

    /// <summary>Installs a genuine PostgreSQL check-violation on every subsequent insert into <c>"Events"</c>.
    /// <c>NOT VALID</c> skips validating the rows already there (this container is shared across every test in
    /// the class, and those rows are the log's own history), but still enforces the constraint on every new
    /// row — exactly the shape a real append failure takes.</summary>
    private Task BlockEventAppendsAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"Events\" ADD CONSTRAINT {BlockAppendConstraint} CHECK (false) NOT VALID"));

    private Task UnblockEventAppendsAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"Events\" DROP CONSTRAINT IF EXISTS {BlockAppendConstraint}"));

    private Task<StopStaging> StagingAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().AsNoTracking().Where(p => p.Id == planId).Select(p => p.Staging).SingleAsync());

    private Task<int> OrderSizeAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Size).SingleAsync());

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
