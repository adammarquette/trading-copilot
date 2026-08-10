using System.Net;
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
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

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

    [Fact]
    public async Task Restart_ShouldBringAStillValidSuggestionBackActive_ButInert()
    {
        // The other side of the expire boundary: a suggestion still INSIDE its validity window survives the restart
        // as Active — but Active is DATA, not a resumed action. The restart transmits nothing and creates no order;
        // the take path re-gates it on any later operator action (R-12, covered by SuggestionTakeIntegrationTests).
        // The restart edge here is that a survivor is neither wrongly voided nor silently taken.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid survivor = await SeedSuggestionAsync(
            accountId, operatorId, SuggestionState.Active,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        long ordersBefore = await OrderCountAsync();

        await WithRestartAsync(async rebooted =>
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse(
                "a valid suggestion is a consistent surface — no fail-safe"));

        (await SuggestionStateAsync(survivor)).Should().Be(
            SuggestionState.Active,
            "a suggestion still inside its window comes back Active — the recovery-expire pass voids only lapsed ones");
        (await OrderCountAsync()).Should().Be(
            ordersBefore, "coming back Active is data, not resumption — nothing was transmitted or taken");
    }

    [Fact]
    public async Task Restart_ShouldKeepClosedLifecycleRows_NeverDeletingStaleOrExpiredVoid()
    {
        // "Nothing is ever deleted" (ADR-0013, R-8): a Stale or ExpiredVoid suggestion is the journal's record of a
        // setup that scratched or lapsed. The restart's rehydration OBSERVES the surface; it never prunes it. Both
        // rows come back present with their state intact — the Stale one sits inside its window, so the recovery
        // expire leaves it (there is no Stale → Active un-drift either), and ExpiredVoid is terminal.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid stale = await SeedSuggestionAsync(
            accountId, operatorId, SuggestionState.Stale,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Guid voided = await SeedSuggestionAsync(
            accountId, operatorId, SuggestionState.ExpiredVoid,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-30));

        await WithRestartAsync(async rebooted =>
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse(
                "closed-lifecycle rows are not an inconsistency"));

        (await SuggestionCountAsync()).Should().Be(
            2, "neither row is pruned by rehydration — the journal keeps them");
        (await SuggestionStateAsync(stale)).Should().Be(
            SuggestionState.Stale,
            "a Stale suggestion inside its window stays Stale — never chased back to Active, never deleted");
        (await SuggestionStateAsync(voided)).Should().Be(
            SuggestionState.ExpiredVoid, "an ExpiredVoid suggestion is terminal and kept");
    }

    [Fact]
    public async Task Restart_ShouldKeepSuggestionsAndDispositionsOwned_AndInvisibleToAnotherOperator()
    {
        // ADR-0013's NAMED QA item: the cross-user-isolation-through-restart proof. Owner A's suggestion and its
        // disposition keep their owner across the restart (R-20), and a second operator can neither see nor act on
        // them — the default-deny filter is re-applied to the REHYDRATED surface, not only the live one.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid ownerA = await OperatorIdAsync();
        Guid suggestionId = await SeedSuggestionAsync(
            accountId, ownerA, SuggestionState.Active,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Guid dispositionId = await SeedPassDispositionAsync(suggestionId, ownerA);
        (string intruderEmail, string intruderPassword) = await CreateSecondOperatorAsync();

        await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
        HttpClient intruder = restarted.CreateClient();
        intruder.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await LoginAsync(intruder, intruderEmail, intruderPassword));

        using HttpResponseMessage view = await intruder.GetAsync($"/suggestions/{suggestionId}");
        view.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "R-20 holds through the restart — another operator cannot even see the owner's suggestion");

        (await SuggestionOwnerAsync(suggestionId)).Should().Be(
            ownerA, "the suggestion keeps its owner across the restart");
        (await DispositionOwnerAsync(dispositionId)).Should().Be(
            ownerA, "and so does its disposition — the journal's owner is never rewritten by rehydration");
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

    private async Task<Guid> SeedPassDispositionAsync(Guid suggestionId, Guid ownerId)
    {
        Guid dispositionId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.SuggestionDispositions.Add(new SuggestionDisposition
            {
                Id = dispositionId,
                UserId = ownerId,
                SuggestionId = suggestionId,
                Kind = SuggestionDispositionKind.Passed,
                Reasons = SuggestionPassReason.None,
                Deviations = SuggestionDeviation.None,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return dispositionId;
    }

    private async Task<(string Email, string Password)> CreateSecondOperatorAsync()
    {
        string email = $"intruder-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        HttpClient owner = await AuthenticatedClientAsync();

        using HttpResponseMessage invite =
            await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        invite.EnsureSuccessStatusCode();
        IssueInvitationResponse? issued =
            await invite.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        using HttpResponseMessage accept = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(issued.Token, password, "Intruder"));
        accept.EnsureSuccessStatusCode();
        return (email, password);
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

    private Task<Guid> SuggestionOwnerAsync(Guid id) => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().Where(s => s.Id == id).Select(s => s.UserId).SingleAsync());

    private Task<Guid> DispositionOwnerAsync(Guid id) => QueryDbAsync(db =>
        db.SuggestionDispositions.IgnoreQueryFilters().Where(d => d.Id == id).Select(d => d.UserId).SingleAsync());

    private Task<int> ActiveSuggestionCountAsync() => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().CountAsync(s => s.State == SuggestionState.Active));

    private Task<int> SuggestionCountAsync() => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().CountAsync());

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
