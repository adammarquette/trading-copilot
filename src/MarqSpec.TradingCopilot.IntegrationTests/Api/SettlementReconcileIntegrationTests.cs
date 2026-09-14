using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>EOD settlement reconcile</b> (gh#194, gh#193, R-13, R-19, R-12, R-20, ADR-0013):
/// the one window each day when the usual source of truth changes underneath the system. The reconcile
/// (<see cref="PositionReconciliationService.ReconcileAsync"/>) yields to <b>venue truth</b>, tags a carried-through
/// position as a <b>settlement re-mark</b> (never a live movement), derives the window from each instrument's
/// session (DST-correct), and <b>declares-unknown</b> when the venue is unreachable rather than presenting a stale
/// live-looking number.
/// </summary>
/// <remarks>
/// The reconcile is a request-scoped, R-20-<b>filtered</b> service that takes <c>now</c> as a parameter (the HTTP
/// endpoint hard-codes the clock). To control both the clock and the owner, the suite constructs the service over a
/// <c>DbContext</c> scoped to a chosen user (the unit-test pattern) — the only way to drive a window at a fixed
/// instant. The re-mark is represented solely by <see cref="PositionMarkBasis"/> (Live / Settlement / Unknown);
/// there is no local position store and no journal, so venue-as-truth and read-only hold <b>by construction</b>.
/// Always-on hosts are stripped by <see cref="SettlementTestPostgresFactory"/>. Traces R-13 · R-19 · R-12 · R-20 · ADR-0013.
/// </remarks>
public class SettlementReconcileIntegrationTests : IClassFixture<SettlementTestPostgresFactory>
{
    private const string VenueKey = "PRAC-50K-101";

    private readonly SettlementTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Summer date → CDT (UTC−5): ES/NQ close 14:30 CT = 19:30 UTC; CL 13:15 = 18:15; GC 12:15 = 17:15.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);
    private sealed class FixedUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }

    public SettlementReconcileIntegrationTests(SettlementTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Venue as source of truth.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_ShouldYieldToVenue_WhenLocalStateDisagrees()
    {
        // The centrepiece. There is no local position store — venue truth is the ONLY truth, surfaced verbatim.
        // The adversarial stub reports a SHORT 3-lot the system never computed; the reconcile must report exactly it.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: -3, averagePrice: 5_012.50m);

        PositionReconciliation? result = await ReconcileAsync(accountId, ownerId, Utc(14, 0)); // 09:00 CT — plain session

        result.Should().NotBeNull();
        result!.Positions.Should().ContainSingle();
        PositionSnapshot position = result.Positions.Single();
        position.Contract.Key.Should().Be("ESM25");
        position.NetQuantity.Should().Be(-3, "the reconcile reports the venue's position verbatim — it never overrides it with local state");
        position.AveragePrice.Value.Should().Be(5_012.50m);
    }

    [Fact]
    public async Task Reconcile_ShouldNotActOnReconciledState_BeforeRevalidating()
    {
        // R-12: reconcile is a pure READ — it never transmits or closes on the way in. Acting is a separate,
        // re-validated step. Proven by construction: the service has no execution path.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        await ReconcileAsync(accountId, ownerId, Utc(20, 0)); // 15:00 CT — inside the ES settlement window

        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "reconcile transmits nothing");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("reconcile closes nothing — it only reads venue truth");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The settlement re-mark.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Settlement_ShouldReflectReMark_WhenPositionCarriedThroughWindow()
    {
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        PositionReconciliation? result = await ReconcileAsync(accountId, ownerId, Utc(20, 0)); // 15:00 CT — in the ES window

        result!.Basis.Should().Be(PositionMarkBasis.Settlement, "a position carried through the settlement window is marked on the settlement basis");
    }

    [Fact]
    public async Task Settlement_ShouldNotPresentReMarkAsLiveMovement_WhenPositionCarriedThrough()
    {
        // The highest-value guard: a settlement re-mark looks exactly like a P&L move and is NOT one. It must be
        // tagged Settlement, never Live, so the operator never reads it as the market moving against them.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        PositionReconciliation? result = await ReconcileAsync(accountId, ownerId, Utc(20, 0)); // 15:00 CT — in the ES window

        result!.Basis.Should().NotBe(PositionMarkBasis.Live, "a settlement re-mark must never be presented as live market movement (R-13)");
        result.Basis.Should().Be(PositionMarkBasis.Settlement);
    }

    [Fact]
    public async Task Settlement_ShouldNotPresentStalePreSettlementMarkAsLive_WhenWindowHasPassed()
    {
        // R-19: once the window has passed, the mark is a fresh LIVE venue read — never a stale pre-settlement value
        // held over and presented as live. (No local cache exists, so freshness holds by construction.)
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        PositionReconciliation? result = await ReconcileAsync(accountId, ownerId, Utc(21, 0)); // 16:00 CT — after every window

        result!.Basis.Should().Be(PositionMarkBasis.Live, "after the window the mark is live again — a fresh venue read, not a stale carry-over");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Window awareness.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Window_ShouldDeriveFromInstrumentSession_WhenInstrumentsSettleAtDifferentTimes()
    {
        // GC (12:15) and CL (13:15) settle earlier than ES/NQ (14:30). A hard-coded single window could never
        // produce Settlement at 12:30 AND 14:45 with a Live gap at 14:20 — that pattern only falls out of
        // per-instrument sessions.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        (await ReconcileAsync(accountId, ownerId, Utc(17, 30)))!.Basis // 12:30 CT — inside GC's window
            .Should().Be(PositionMarkBasis.Settlement, "GC's earlier session opens its settlement window at 12:15 CT");
        (await ReconcileAsync(accountId, ownerId, Utc(19, 20)))!.Basis // 14:20 CT — after CL (14:15), before ES (14:30)
            .Should().Be(PositionMarkBasis.Live, "between the CL and ES windows the mark is live again");
        (await ReconcileAsync(accountId, ownerId, Utc(19, 45)))!.Basis // 14:45 CT — inside ES's window
            .Should().Be(PositionMarkBasis.Settlement, "ES's later session opens its settlement window at 14:30 CT");
    }

    [Fact]
    public async Task Window_ShouldBeDstCorrect_WhenClockCrossesTransition()
    {
        // The same UTC instant (21:00) is 16:00 CDT in July (window passed → Live) but 15:00 CST in January
        // (inside the ES window → Settlement). Only a DST-aware clock yields two different bases for one UTC value.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        DateTimeOffset summer = new(2026, 7, 15, 21, 0, 0, TimeSpan.Zero); // 16:00 CDT
        DateTimeOffset winter = new(2026, 1, 15, 21, 0, 0, TimeSpan.Zero); // 15:00 CST

        (await ReconcileAsync(accountId, ownerId, summer))!.Basis
            .Should().Be(PositionMarkBasis.Live, "21:00 UTC is 16:00 CDT in July — past the ES window");
        (await ReconcileAsync(accountId, ownerId, winter))!.Basis
            .Should().Be(PositionMarkBasis.Settlement, "the SAME 21:00 UTC is 15:00 CST in January — inside the ES window; only a DST-aware clock tells them apart");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Degradation.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_ShouldDeclareUnknown_WhenVenueUnreachableDuringWindow()
    {
        // Venue truth unobtainable during the window → declare-unknown with NO positions. Never a stale
        // live-looking number. Fail-safe is loud emptiness, not a confident wrong answer.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);
        VenueFactory.MakeVenueUnreachable();

        PositionReconciliation? result = await ReconcileAsync(accountId, ownerId, Utc(20, 0)); // 15:00 CT — in the window

        result.Should().NotBeNull();
        result!.Basis.Should().Be(PositionMarkBasis.Unknown, "an unreachable venue yields Unknown, not a stale mark");
        result.Positions.Should().BeEmpty("declared-unknown presents no positions rather than a stale live-looking number");
    }

    [Fact]
    public async Task AutoFlatten_ShouldStillVerifyFlat_WhenWindowOverlapsDeadline()
    {
        // R-13 regression: the settlement change must not weaken "zero busted flattens". At an instant inside BOTH
        // the ES firing window and its settlement window, auto-flatten still closes + verifies flat, and the
        // reconcile independently tags Settlement — two separate passes, neither suppressing the other.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        (await ReconcileAsync(accountId, ownerId, Utc(19, 45)))!.Basis // 14:45 CT — overlap window
            .Should().Be(PositionMarkBasis.Settlement, "the overlap instant is inside the settlement window");

        int executedBefore = await EventCountAsync(AutoFlattenService.ExecutedEventType);
        await RunAutoFlattenPassAsync(Utc(19, 45));

        (await EventCountAsync(AutoFlattenService.ExecutedEventType) - executedBefore)
            .Should().Be(1, "auto-flatten still closes + verifies flat in the overlap window — the settlement change does not weaken R-13");
        VenueFactory.ClosePositionCalls.Should().Contain(call => call.ContractKey == "ESM25");
    }

    [Fact]
    public async Task Reconcile_ShouldPreserveOwnership_WhenRunningAsBackgroundPlumbing()
    {
        // R-20 survives: the reconcile reads through the default-deny filter, so another operator cannot reconcile
        // the owner's account — it is simply not found.
        (Guid accountId, Guid ownerId) = await FreshAccountAsync();
        VenueFactory.SeedPosition(VenueKey, "ESM25", netQuantity: 2);

        (await ReconcileAsync(accountId, ownerId, Utc(14, 0))).Should().NotBeNull("the owner can reconcile their own account");
        (await ReconcileAsync(accountId, Guid.NewGuid(), Utc(14, 0)))
            .Should().BeNull("another operator cannot reconcile the owner's account — R-20 default-deny holds");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<(Guid AccountId, Guid OwnerId)> FreshAccountAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        Guid ownerId = await OperatorIdAsync();
        Guid accountId = await SetupAccountAsync(client);
        return (accountId, ownerId);
    }

    private async Task<PositionReconciliation?> ReconcileAsync(Guid accountId, Guid ownerId, DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        DbContextOptions<TradingCopilotDbContext> options = sp.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext db = new(options, new FixedUser(ownerId));

        PositionReconciliationService service = new(
            db,
            sp.GetRequiredService<IProjectXVenueFactory>(),
            sp.GetRequiredService<IOptions<ProjectXConnectionOptions>>(),
            sp.GetRequiredService<IOptions<FlattenOptions>>(),
            sp.GetRequiredService<ILogger<PositionReconciliationService>>());

        return await service.ReconcileAsync(accountId, now, CancellationToken.None);
    }

    private async Task RunAutoFlattenPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
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
            "/firms", new CreateFirmRequest($"Topstep-Settle-{Guid.NewGuid():N}", FirmType.PropFirm));
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

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<int> EventCountAsync(string type) =>
        QueryDbAsync(db => db.Events.CountAsync(candidate => candidate.Type == type));

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
