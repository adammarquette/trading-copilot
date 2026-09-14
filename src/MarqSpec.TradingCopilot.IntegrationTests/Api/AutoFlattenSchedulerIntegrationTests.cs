using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>primary</b> auto-flatten scheduler (gh#186, gh#185, R-13, ADR-0013) against
/// real PostgreSQL: the safety net that closes open positions at each instrument's per-market deadline and
/// journals every action to the event log. Driven through the public <see cref="AutoFlattenService.RunPassAsync"/>
/// at a controlled instant (the service is clock-free — <c>now</c> is a method parameter), with the adversarial
/// venue stub seeding the open positions and echoing the venue's post-close truth.
/// </summary>
/// <remarks>
/// The always-on hosted services are stripped by <see cref="FlattenTestPostgresFactory"/> so the only pass is the
/// test's own; each test resets seeded positions and account rows so <c>RunPassAsync</c> (which enumerates every
/// account, R-20-bypassed as background plumbing) acts on exactly one. Redundant watchdog + rejected-order is
/// gh#188; settlement reconcile is gh#193. Traces R-13 · ADR-0013.
/// </remarks>
public class AutoFlattenSchedulerIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Summer date → CDT (UTC−5): ES/NQ close 14:30 CT = 19:30 UTC; CL 13:15; GC 12:15. Mirrors the unit tests.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public AutoFlattenSchedulerIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RunPass_ShouldCloseThePosition_AndJournalExecuted_AtTheDeadline()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenService.ExecutedEventType);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        await RunPassAsync(Utc(19, 45)); // 14:45 CT — past the 14:30 ES deadline, inside the firing window

        (await EventCountAsync(AutoFlattenService.ExecutedEventType) - before).Should().Be(1, "the open ES position is closed and journalled at its deadline");
        VenueFactory.ClosePositionCalls.Should().ContainSingle(call => call.ContractKey == "ESM25");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "auto-flatten only ever reduces exposure — it never places an opening order");
        (await LatestReasonAsync(AutoFlattenService.ExecutedEventType)).Should().Contain(account);
    }

    [Fact]
    public async Task RunPass_ShouldNotClose_BeforeTheDeadline()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenService.ExecutedEventType);

        await RunPassAsync(Utc(17, 0)); // 12:00 CT — two and a half hours early

        (await EventCountAsync(AutoFlattenService.ExecutedEventType) - before).Should().Be(0, "nothing is due yet");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("no close before the deadline");
    }

    [Fact]
    public async Task RunPass_ShouldWarn_WhenTheDeadlineIsNear()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenService.WarningEventType);

        await RunPassAsync(Utc(19, 15)); // 14:15 CT — fifteen minutes before the deadline

        (await EventCountAsync(AutoFlattenService.WarningEventType) - before).Should().Be(1, "an escalating warning precedes the flatten (R-13)");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("warn, do not yet close");
    }

    [Fact]
    public async Task RunPass_ShouldJournalMissed_WhenTheFiringWindowHasPassed()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenService.MissedEventType);

        await RunPassAsync(Utc(21, 0)); // 16:00 CT — 90 min past 14:30, beyond the one-hour firing window

        (await EventCountAsync(AutoFlattenService.MissedEventType) - before).Should().Be(1, "firing blind into the settlement window is worse than escalating (ADR-0013)");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("the window has passed — it must not close, but must not be silent");
    }

    [Fact]
    public async Task RunPass_ShouldRetryThenEscalate_WhenThePositionSurvivesTheClose()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25"); // the venue keeps reporting it open (reject / partial fill)

        int before = await EventCountAsync(AutoFlattenService.EscalatedEventType);

        await RunPassAsync(Utc(19, 45));

        (await EventCountAsync(AutoFlattenService.EscalatedEventType) - before).Should().Be(1, "a surviving position after the attempt cap escalates loudly");
        VenueFactory.ClosePositionCalls.Count(call => call.ContractKey == "ESM25")
            .Should().Be(3, "it retries to the default attempt cap (MaxFlattenAttempts = 3) before escalating");
    }

    [Fact]
    public async Task RunPass_ShouldFlagUnconfigured_ForAProductWithNoDeadline()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "YMM25", netQuantity: 1); // YM has no configured/default deadline

        int before = await EventCountAsync(AutoFlattenService.UnconfiguredEventType);

        await RunPassAsync(Utc(19, 45));

        (await EventCountAsync(AutoFlattenService.UnconfiguredEventType) - before).Should().Be(1, "an open position with no deadline must never be silent (R-13)");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("we cannot time its flatten, so we do not close — we flag");
    }

    [Fact]
    public async Task RunPass_ShouldFlagDisabled_ForADisabledMarketWithAnOpenPosition()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "CLM25", netQuantity: -1); // CL is declared disabled by the factory

        int before = await EventCountAsync(AutoFlattenService.DisabledEventType);

        await RunPassAsync(Utc(19, 45)); // past CL's 13:15 deadline

        (await EventCountAsync(AutoFlattenService.DisabledEventType) - before).Should().Be(1, "a disabled market with a live position stays visible, never a silent no-op (R-13)");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("disabled means do not close — but do flag");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task RunPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    /// <summary>Clears seeded positions + every account row, then stands up one tradeable account and returns its
    /// venue key — so the process-global <c>RunPassAsync</c> acts on exactly this test's account.</summary>
    private async Task<string> FreshAccountWithKeyAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Flatten-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        return accounts.First(a => a.CanTrade).VenueAccountKey;
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

    private Task<int> EventCountAsync(string type) =>
        QueryDbAsync(db => db.Events.CountAsync(candidate => candidate.Type == type));

    private Task<string> LatestReasonAsync(string type) => QueryDbAsync(db =>
        db.Events.Where(candidate => candidate.Type == type)
            .OrderByDescending(candidate => candidate.Sequence)
            .Select(candidate => candidate.Payload)
            .FirstAsync());

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
