using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The suggestion <b>lifecycle across a restart</b> (gh#552, of the [A3] epic gh#17; R-4/R-8/R-12/R-20, ADR-0013):
/// the fail-safe intent proven independently of the implementation, on real Postgres. A suggestion whose validity
/// elapsed <b>while the process was down</b> comes back <c>ExpiredVoid</c>, never <c>Active</c>; a still-valid one
/// comes back <c>Active</c> but inert; and nothing that closed a lifecycle is ever deleted.
/// </summary>
/// <remarks>
/// The restart harness is <see cref="RehydrationTestPostgresFactory"/>: its Postgres container outlives the host,
/// so a second host built via <c>CreateHost</c> re-runs <c>StartupTasks</c> — migrate → kill-switch rehydrate →
/// the gh#545 <b>recovery-expire pass</b> (the same <c>ExpireDueAsync</c> transition the steady-state sweep runs,
/// on a relational provider only) → decision-state rehydrate — against the <b>same database</b>. That IS the
/// restart, and it is what voids a lapsed suggestion before the surface is counted. Hosted sweeps are stripped by
/// the fixture, so nothing but the restart perturbs the seeded state. Each test resets the surface first, because
/// the guard runs deployment-wide over the shared container. Traces R-4 · R-8 · R-12 · R-20 · ADR-0013.
/// </remarks>
public class SuggestionLifecycleRestartIntegrationTests : IClassFixture<RehydrationTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const decimal Entry = 5_000m;

    private readonly RehydrationTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SuggestionLifecycleRestartIntegrationTests(RehydrationTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Restart_ShouldVoidASuggestionThatLapsedDuringDowntime_NotBringItBackActive()
    {
        // ADR-0013's explicitly REJECTED alternative is "auto-resume suggestions as-was after an outage". The
        // property that must hold instead: an Active suggestion whose validity window elapsed WHILE THE PROCESS WAS
        // DOWN comes back ExpiredVoid, never Active. StartupTasks runs the gh#545 recovery-expire pass before the
        // rehydrator counts the surface, so the restart itself voids it — a stale setup is never put back in front
        // of the operator as actionable, and nothing is auto-taken.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();

        // Active when the process stopped, but its window has since elapsed (ExpiresAt in the past). CreatedAt sits
        // before ExpiresAt to clear the CK_Suggestions_ExpiresAfterCreated CHECK.
        Guid lapsed = await SeedSuggestionAsync(
            accountId, operatorId, SuggestionState.Active,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        long ordersBefore = await OrderCountAsync();

        await WithRestartAsync(async rebooted =>
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse(
                "an expired suggestion carries no risk — it must not trip the startup fail-safe"));

        (await SuggestionStateAsync(lapsed)).Should().Be(
            SuggestionState.ExpiredVoid,
            "a suggestion whose window elapsed during downtime comes back ExpiredVoid, not Active "
            + "(ADR-0013: expire and re-validate, never auto-resume)");
        (await ActiveSuggestionCountAsync()).Should().Be(
            0, "the decision surface counts it as inactive — the rehydrator's Active count is taken after the expire pass");
        (await OrderCountAsync()).Should().Be(
            ordersBefore, "nothing was auto-taken — no order is resurrected from the lapsed suggestion");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness — mirrors StateRehydrationIntegrationTests (gh#223): the container outlives the host, so CreateHost
    // re-runs StartupTasks against the same database, and that is the "restart".
    // ---------------------------------------------------------------------------------------------------------

    private async Task<(Guid AccountId, string VenueKey)> FreshAccountAsync()
    {
        await ResetStateAsync();
        HttpClient client = await AuthenticatedClientAsync();
        return await SetupAccountAsync(client);
    }

    private async Task ResetStateAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            // FK-safe order: a disposition points at its suggestion, so it goes first. The guard runs
            // deployment-wide over the shared container, so a prior test's rows would otherwise be swept by the
            // recovery pass and skew the active count.
            await db.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.KillSwitchStates.ExecuteDeleteAsync();
        });
    }

    private async Task WithRestartAsync(Func<HttpClient, Task> body)
    {
        await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
        HttpClient rebooted = restarted.CreateClient(); // building the host re-runs StartupTasks — the "restart"
        rebooted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(rebooted, PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        await body(rebooted);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(client, PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        return client;
    }

    private async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private async Task<(Guid AccountId, string VenueKey)> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Lifecycle-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        using HttpResponseMessage discover =
            await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(account => account.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task<Guid> SeedSuggestionAsync(
        Guid accountId,
        Guid operatorId,
        SuggestionState state,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Guid suggestionId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = Contract,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = Entry,
                StopPrice = 4_990m,
                TargetPrice = 5_020m,
                Mode = TradingMode.Practice,
                State = state,
                Rationale = "seeded",
                CitedIndicator = "rsi",
                CitedPeriod = 14,
                CitedResolutionMinutes = 1,
                Confidence = 50,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync();
        });
        return suggestionId;
    }

    private async Task<KillSwitchStateResponse> GetKillSwitchAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/kill-switch");
        response.EnsureSuccessStatusCode();
        KillSwitchStateResponse? state = await response.Content.ReadFromJsonAsync<KillSwitchStateResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(state);
        return state;
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<SuggestionState> SuggestionStateAsync(Guid id) => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().Where(s => s.Id == id).Select(s => s.State).SingleAsync());

    private Task<int> ActiveSuggestionCountAsync() => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().CountAsync(s => s.State == SuggestionState.Active));

    private Task<long> OrderCountAsync() => QueryDbAsync(db => db.Orders.IgnoreQueryFilters().LongCountAsync());

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
