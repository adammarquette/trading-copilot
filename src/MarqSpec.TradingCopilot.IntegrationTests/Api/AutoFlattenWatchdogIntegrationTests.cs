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
/// Integration coverage for the <b>redundant auto-flatten watchdog</b> (gh#188, gh#187, R-13, ADR-0013) against
/// real PostgreSQL: the independent second tier that steps in only on the primary scheduler's <i>failure</i> — a
/// position still open past its deadline <b>plus a grace window</b> — closes it once per pass, and journals a
/// rejected close rather than ever silently giving up. Driven through the public
/// <see cref="AutoFlattenWatchdogService.RunPassAsync"/> at a controlled instant with the shared venue-position
/// stub; the always-on hosts are suppressed by <see cref="FlattenTestPostgresFactory"/>. Traces R-13 · ADR-0013.
/// </summary>
public class AutoFlattenWatchdogIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Summer date → CDT (UTC−5): ES 14:30 CT = 19:30 UTC; default watchdog grace is 2 minutes.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public AutoFlattenWatchdogIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RunPass_ShouldBackstopAndJournalSaved_WhenThePrimaryLeftItOpenPastDeadlineAndGrace()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenWatchdogService.SavedEventType);
        int placedBefore = VenueFactory.TotalPlacedOrderCount;

        await RunPassAsync(Utc(19, 45)); // 14:45 CT — 15 min past 14:30, well beyond the 2-min grace

        (await EventCountAsync(AutoFlattenWatchdogService.SavedEventType) - before).Should().Be(1, "the watchdog backstops what the primary left open past the deadline + grace");
        VenueFactory.ClosePositionCalls.Should().ContainSingle(call => call.ContractKey == "ESM25");
        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "the watchdog only reduces exposure — it never opens an order");
    }

    [Fact]
    public async Task RunPass_ShouldDeferToThePrimary_WithinTheGraceWindow()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int savedBefore = await EventCountAsync(AutoFlattenWatchdogService.SavedEventType);
        int rejectedBefore = await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType);

        await RunPassAsync(Utc(19, 31)); // 14:31 CT — 1 min past the deadline, still inside the 2-min grace

        (await EventCountAsync(AutoFlattenWatchdogService.SavedEventType) - savedBefore).Should().Be(0);
        (await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType) - rejectedBefore).Should().Be(0);
        VenueFactory.ClosePositionCalls.Should().BeEmpty("within grace the two tiers must not fight — the watchdog defers to the primary");
    }

    [Fact]
    public async Task RunPass_ShouldJournalRejected_WhenTheCloseLeavesExposure()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25"); // the venue still shows exposure after the close

        int before = await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType);

        await RunPassAsync(Utc(19, 45));

        (await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType) - before).Should().Be(1, "a close that leaves exposure is journalled, never a silent give-up");
        VenueFactory.ClosePositionCalls.Count(call => call.ContractKey == "ESM25")
            .Should().Be(1, "the watchdog makes ONE attempt per pass — its persistence is the next pass, not a tight retry loop");
    }

    [Fact]
    public async Task RunPass_ShouldJournalRejected_WhenTheVenueRejectsTheClose()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseThrow("ESM25"); // the venue hard-rejects the close order

        int before = await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType);

        await RunPassAsync(Utc(19, 45));

        (await EventCountAsync(AutoFlattenWatchdogService.RejectedEventType) - before).Should().Be(1, "a rejected close is journalled and retried next pass — the fault neither stalls the pass nor is swallowed");
        VenueFactory.ClosePositionCalls.Should().Contain(call => call.ContractKey == "ESM25", "the watchdog did attempt the close");
    }

    [Fact]
    public async Task RunPass_ShouldJournalCritical_WhenTheFiringWindowHasPassed()
    {
        string account = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);

        int before = await EventCountAsync(AutoFlattenWatchdogService.CriticalEventType);

        await RunPassAsync(Utc(21, 0)); // 16:00 CT — past the one-hour firing window

        (await EventCountAsync(AutoFlattenWatchdogService.CriticalEventType) - before).Should().Be(1, "exposure past the firing window is a critical escalation, not a blind late close");
        VenueFactory.ClosePositionCalls.Should().BeEmpty("past the window it must not close blind — it escalates");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task RunPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenWatchdogService service = scope.ServiceProvider.GetRequiredService<AutoFlattenWatchdogService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    private async Task<string> FreshAccountWithKeyAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Watchdog-{Guid.NewGuid():N}", FirmType.PropFirm));
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
