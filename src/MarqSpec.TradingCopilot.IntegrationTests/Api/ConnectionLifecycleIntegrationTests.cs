using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for connection creation, account discovery, and the per-account stage override
/// (gh#142, gh#7, gh#60/gh#76) against real PostgreSQL. Realigned to the real route map by gh#160: this covers
/// the endpoints that exist — `POST /connections`, `GET /connections`, `POST /connections/{id}/accounts/discover`,
/// `GET /connections/{id}/accounts`, `PUT`/`DELETE /accounts/{id}/stage`. Credential rotation and connection
/// soft-delete are missing *features*, tracked for the Coding Agent in gh#210, not here.
/// </summary>
/// <remarks>
/// The venue seam is the sole sanctioned double, and it is **adversarial**: it reports
/// <see cref="TradingMode.Live"/> for every account regardless of stage, so any test that saw a discovered
/// account's mode as anything but what the firm's declared conventions resolve would be catching the exact
/// gh#60 failure — a mode trusted from the venue rather than computed from the operator's declaration.
/// </remarks>
public class ConnectionLifecycleIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ConnectionLifecycleIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateConnection_ThenDiscover_ShouldPersistRoster_WithModeFromConventions()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, "Topstep-ConnDiscover");
        await DeclareConventionsAsync(client,
            firmId,
            new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false));
        Guid connectionId = await CreateConnectionAsync(client, firmId);

        List<AccountResponse> discovered = await DiscoverAsync(client, connectionId);
        discovered.Should().NotBeEmpty("the adversarial venue publishes a roster");

        // The roster persists and is readable back through the by-connection read.
        List<AccountResponse> roster = await ListAccountsAsync(client, connectionId);
        roster.Select(a => a.VenueAccountKey).Should().BeEquivalentTo(discovered.Select(a => a.VenueAccountKey),
            "discovery persists the roster; the GET serves exactly what was stored");

        // The gh#60 property: mode comes from the firm's conventions, NEVER the venue's flag (the stub says Live
        // for all). A Practice-stage tradeable account resolves to Practice.
        AccountResponse tradeable = roster.First(a => a.CanTrade);
        tradeable.Mode.Should().Be(TradingMode.Practice, "a no-capital-at-risk stage resolves to Practice under the firm's conventions");
        tradeable.Mode.Should().NotBe(TradingMode.Live, "the venue reported Live for every account; the persisted mode must not trust it");

        // Fail-closed: a name the conservative resolver cannot recognise stays Unknown → Undeclared, tradeable nowhere.
        AccountResponse unrecognised = roster.Single(a => a.Name == "UNKNOWN-NAME-999");
        unrecognised.Stage.Should().Be(AccountStage.Unknown);
        unrecognised.Mode.Should().Be(TradingMode.Undeclared, "the resolver never guesses — an unrecognised name is Undeclared");
    }

    [Fact]
    public async Task CreateConnection_ShouldAppearInList_AndRejectDuplicatePerFirmPlatform()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, "Topstep-ConnList");

        Guid connectionId = await CreateConnectionAsync(client, firmId);

        // It is listable.
        List<ConnectionResponse> connections = await ListConnectionsAsync(client);
        connections.Should().Contain(c => c.Id == connectionId && c.FirmId == firmId && c.Platform == "projectx");

        // ADR-0016: one login per firm × platform. A second connection on the same pair is refused.
        using HttpResponseMessage duplicate = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firmId, "projectx", "topstep-main"));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict, "one login per firm × platform (ADR-0016)");
    }

    [Fact]
    public async Task SetAccountStageOverride_ShouldResolveModeByFirmConventions_AndRejectUnknown()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, "Topstep-StageOverride");
        await DeclareConventionsAsync(client,
            firmId,
            new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true));
        Guid connectionId = await CreateConnectionAsync(client, firmId);
        Guid accountId = (await DiscoverAsync(client, connectionId)).First(a => a.CanTrade).Id;

        // Override to a declared capital-at-risk stage → the mode follows the firm's convention for it (Live).
        using HttpResponseMessage overridden = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/stage", new SetStageOverrideRequest(AccountStage.Funded));
        overridden.StatusCode.Should().Be(HttpStatusCode.OK);
        AccountResponse afterOverride = await ReadAccountResponseAsync(overridden);
        afterOverride.StageOverride.Should().Be(AccountStage.Funded);
        afterOverride.Mode.Should().Be(TradingMode.Live, "Funded is declared capital-at-risk, so the effective mode is Live");

        // Unknown is not a declaration — it is the absence of one, and is refused (clearing is how you say "I don't know").
        using HttpResponseMessage unknown = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/stage", new SetStageOverrideRequest(AccountStage.Unknown));
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Unknown is not a declarable stage");
    }

    [Fact]
    public async Task ClearAccountStageOverride_ShouldRevertToTheResolvedStage()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, "Topstep-ClearOverride");
        await DeclareConventionsAsync(client,
            firmId,
            new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true));
        Guid connectionId = await CreateConnectionAsync(client, firmId);

        AccountResponse atDiscovery = (await DiscoverAsync(client, connectionId)).First(a => a.CanTrade);
        atDiscovery.StageOverride.Should().BeNull("discovery sets no operator override");
        TradingMode resolvedMode = atDiscovery.Mode;

        // Override away from the resolved stage…
        using HttpResponseMessage overridden = await client.PutAsJsonAsync(
            $"/accounts/{atDiscovery.Id}/stage", new SetStageOverrideRequest(AccountStage.Funded));
        (await ReadAccountResponseAsync(overridden)).Mode.Should().Be(TradingMode.Live, "the override moved the mode");

        // …then clear it: the override is dropped and the mode falls back to the resolver's reading.
        using HttpResponseMessage cleared = await client.DeleteAsync($"/accounts/{atDiscovery.Id}/stage");
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        AccountResponse afterClear = await ReadAccountResponseAsync(cleared);
        afterClear.StageOverride.Should().BeNull("clearing drops the operator's override");
        afterClear.Mode.Should().Be(resolvedMode, "the mode reverts to what the firm's conventions resolve for the discovered stage");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Credential rotation — PUT /connections/{id}/credentials (gh#229 ⇒ gh#210, R-17, R-20, ADR-0015).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateCredentials_ShouldRotateKey_WhenCallerOwnsConnection()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-Rotate-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionWithKeyAsync(client, firmId, LegacyKey);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/connections/{connectionId}/credentials", new RotateCredentialsRequest(HeldKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ConnectionResponse? updated = await response.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        updated!.CredentialKey.Should().Be(HeldKey, "the response reflects the repointed key");
        (await CredentialKeyAsync(connectionId)).Should().Be(HeldKey, "the rotated key is persisted");
    }

    [Fact]
    public async Task UpdateCredentials_ShouldReinitialiseVenueSession_WhenKeyRotated()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-Reinit-{Guid.NewGuid():N}");
        await DeclareConventionsAsync(client, firmId, new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false));
        Guid connectionId = await CreateConnectionWithKeyAsync(client, firmId, LegacyKey);

        // Under the OLD (unheld) key the venue path refuses — the process holds `topstep-main`, not `legacy-key`.
        using HttpResponseMessage beforeRotation = await client.PostAsync($"/connections/{connectionId}/accounts/discover", content: null);
        beforeRotation.StatusCode.Should().Be(HttpStatusCode.Conflict, "the venue path refuses a key this process does not hold");

        (await RotateAsync(client, connectionId, HeldKey)).EnsureSuccessStatusCode();

        // After rotation the venue path uses the NEW key on the very next call — no stale session keeps the old one.
        using HttpResponseMessage afterRotation = await client.PostAsync($"/connections/{connectionId}/accounts/discover", content: null);
        afterRotation.StatusCode.Should().Be(HttpStatusCode.OK, "the rotated key takes effect immediately on the venue path");
    }

    [Fact]
    public async Task UpdateCredentials_ShouldNeverPersistSecret_WhenKeyRotated()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-NoSecret-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionWithKeyAsync(client, firmId, LegacyKey);

        (await RotateAsync(client, connectionId, HeldKey)).EnsureSuccessStatusCode();

        // ADR-0015: the column NAMES an env-held credential set; it is not the secret. The stored value is exactly
        // the key name the operator sent — never a secret, hash, or transformed credential. Asserted by construction
        // against the raw row: a rotation that derived/stored anything but the verbatim name would fail here.
        (await CredentialKeyAsync(connectionId)).Should().Be(HeldKey,
            "the stored credential key is the verbatim env-entry name — no secret material is derived or persisted");
    }

    [Fact]
    public async Task UpdateCredentials_ShouldReturn404_WhenConnectionBelongsToAnotherOperator()
    {
        HttpClient owner = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(owner, $"Topstep-RotateR20-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionWithKeyAsync(owner, firmId, LegacyKey);

        HttpClient intruder = await SecondOperatorClientAsync();
        using HttpResponseMessage response = await intruder.PutAsJsonAsync(
            $"/connections/{connectionId}/credentials", new RotateCredentialsRequest(HeldKey));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20 default-deny: another operator's connection is invisible — 404, never 403");
        (await CredentialKeyAsync(connectionId)).Should().Be(LegacyKey, "the owner's connection is untouched");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Soft-delete — DELETE /connections/{id} (gh#229 ⇒ gh#210, R-17, R-20, ADR-0015).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteConnection_ShouldDeactivateRatherThanRemove_WhenCalled()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-SoftDelete-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionAsync(client, firmId);

        using HttpResponseMessage response = await client.DeleteAsync($"/connections/{connectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ConnectionExistsAsync(connectionId)).Should().BeTrue("a soft delete leaves the row in place — nothing is removed");
        (await ConnectionIsActiveAsync(connectionId)).Should().BeFalse("the surviving row is marked inactive");
    }

    [Fact]
    public async Task DeleteConnection_ShouldCascadeDeactivationToChildAccounts_WhenCalled()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-Cascade-{Guid.NewGuid():N}");
        await DeclareConventionsAsync(client, firmId, new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false));
        Guid connectionId = await CreateConnectionAsync(client, firmId);
        (await DiscoverAsync(client, connectionId)).Should().NotBeEmpty("the roster persisted");

        (await DeleteConnectionAsync(client, connectionId)).EnsureSuccessStatusCode();

        IReadOnlyList<bool> childActive = await AccountActiveStatesAsync(connectionId);
        childActive.Should().NotBeEmpty("the discovered child accounts persisted");
        childActive.Should().OnlyContain(active => active == false, "deactivation cascades to every child account");
    }

    [Fact]
    public async Task DeleteConnection_ShouldPreserveJournalRows_WhenDeactivated()
    {
        // The highest-value guard: a soft delete must NOT take the journal with it. Orders (and the trades / gate
        // decisions) referencing the deactivated account are the audit trail — a cascade that removed them would be
        // invisible until an audit needed the rows.
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-Journal-{Guid.NewGuid():N}");
        await DeclareConventionsAsync(client, firmId, new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false));
        Guid connectionId = await CreateConnectionAsync(client, firmId);
        Guid accountId = (await DiscoverAsync(client, connectionId)).First(account => account.CanTrade).Id;
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedJournalOrderAsync(accountId, operatorId);

        (await DeleteConnectionAsync(client, connectionId)).EnsureSuccessStatusCode();

        (await OrderExistsAsync(orderId)).Should().BeTrue(
            "a soft delete preserves the journal — an order referencing the deactivated account survives for audit");
    }

    [Fact]
    public async Task DeleteConnection_ShouldRefuseNewOrders_WhenConnectionDeactivated()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-RefuseSend-{Guid.NewGuid():N}");
        await DeclareConventionsAsync(client, firmId, new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false));
        Guid connectionId = await CreateConnectionAsync(client, firmId);
        Guid accountId = (await DiscoverAsync(client, connectionId)).First(account => account.CanTrade).Id;
        await DeclareRiskProfileAsync(client, accountId);
        (await DeleteConnectionAsync(client, connectionId)).EnsureSuccessStatusCode();

        using HttpResponseMessage send = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());

        // DEFECT gh#322: the send / compose path never consults Connection.IsActive or Account.IsActive, so a
        // deactivated connection STILL transmits orders to the venue (R-17 soft-delete bypass). Pins the OBSERVED
        // behaviour until #322 lands; the fix flips this to a refusal (409/422) and this test becomes its regression
        // guard. A deactivated connection must not remain a usable send path — deactivation is meaningless otherwise.
        send.StatusCode.Should().Be(HttpStatusCode.OK,
            "DEFECT gh#322: deactivation is not enforced on the send path — a retired connection still routes orders");
    }

    [Fact]
    public async Task DeleteConnection_ShouldReturn404_WhenConnectionBelongsToAnotherOperator()
    {
        HttpClient owner = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(owner, $"Topstep-DeleteR20-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionAsync(owner, firmId);

        HttpClient intruder = await SecondOperatorClientAsync();
        using HttpResponseMessage response = await intruder.DeleteAsync($"/connections/{connectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20 default-deny: another operator cannot deactivate the owner's connection");
        (await ConnectionIsActiveAsync(connectionId)).Should().BeTrue("the owner's connection is untouched");
    }

    [Fact]
    public async Task DeleteConnection_ShouldBeIdempotent_WhenCalledTwice()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid firmId = await CreateFirmAsync(client, $"Topstep-Idempotent-{Guid.NewGuid():N}");
        Guid connectionId = await CreateConnectionAsync(client, firmId);

        (await DeleteConnectionAsync(client, connectionId)).StatusCode.Should().Be(HttpStatusCode.OK);
        using HttpResponseMessage second = await client.DeleteAsync($"/connections/{connectionId}");

        second.StatusCode.Should().Be(HttpStatusCode.OK, "deactivating twice is not an error — a retry after a dropped response must still succeed");
        (await ConnectionIsActiveAsync(connectionId)).Should().BeFalse("the connection stays deactivated");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private const string HeldKey = "topstep-main";
    private const string LegacyKey = "legacy-key";

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

    private async Task<Guid> CreateFirmAsync(HttpClient client, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(name, FirmType.PropFirm));
        response.EnsureSuccessStatusCode();
        FirmResponse? firm = await response.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);
        return firm.Id;
    }

    private async Task DeclareConventionsAsync(HttpClient client, Guid firmId, params StageConventionDto[] conventions)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/firms/{firmId}/conventions", new DeclareConventionsRequest(conventions));
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreateConnectionAsync(HttpClient client, Guid firmId)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firmId, "projectx", "topstep-main"));
        response.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Id;
    }

    private async Task<List<AccountResponse>> DiscoverAsync(HttpClient client, Guid connectionId)
    {
        using HttpResponseMessage response = await client.PostAsync($"/connections/{connectionId}/accounts/discover", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "discovery against the venue stub succeeds");
        List<AccountResponse>? accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts;
    }

    private async Task<List<AccountResponse>> ListAccountsAsync(HttpClient client, Guid connectionId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/connections/{connectionId}/accounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<AccountResponse>? accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts;
    }

    private async Task<List<ConnectionResponse>> ListConnectionsAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/connections");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<ConnectionResponse>? connections = await response.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);
        return connections;
    }

    private async Task<AccountResponse> ReadAccountResponseAsync(HttpResponseMessage response)
    {
        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(account);
        return account;
    }

    // --- gh#229 helpers ---

    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    private async Task<Guid> CreateConnectionWithKeyAsync(HttpClient client, Guid firmId, string credentialKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firmId, "projectx", credentialKey));
        response.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Id;
    }

    private static Task<HttpResponseMessage> RotateAsync(HttpClient client, Guid connectionId, string credentialKey) =>
        client.PutAsJsonAsync($"/connections/{connectionId}/credentials", new RotateCredentialsRequest(credentialKey));

    private static Task<HttpResponseMessage> DeleteConnectionAsync(HttpClient client, Guid connectionId) =>
        client.DeleteAsync($"/connections/{connectionId}");

    private async Task<HttpClient> SecondOperatorClientAsync()
    {
        string email = $"intruder-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        HttpClient owner = await AuthenticatedClientAsync();

        using HttpResponseMessage invite = await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        invite.EnsureSuccessStatusCode();
        IssueInvitationResponse? issued = await invite.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        using HttpResponseMessage accept = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(issued.Token, password, "Intruder"));
        accept.EnsureSuccessStatusCode();

        HttpClient intruder = _factory.CreateClient();
        using HttpResponseMessage login = await intruder.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        login.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await login.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        intruder.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return intruder;
    }

    private Task DeclareRiskProfileAsync(HttpClient client, Guid accountId) =>
        client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 1_000m,
                AccountProfitTarget: 3_000m,
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

    private async Task<Guid> SeedJournalOrderAsync(Guid accountId, Guid userId)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = userId,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = OrderStatus.Filled,
                Mode = TradingMode.Practice,
                VenueOrderKey = "JOURNAL-1",
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private Task<string> CredentialKeyAsync(Guid connectionId) => QueryDbAsync(db =>
        db.Connections.IgnoreQueryFilters().Where(c => c.Id == connectionId).Select(c => c.CredentialKey).SingleAsync());

    private Task<bool> ConnectionExistsAsync(Guid connectionId) => QueryDbAsync(db =>
        db.Connections.IgnoreQueryFilters().AnyAsync(c => c.Id == connectionId));

    private Task<bool> ConnectionIsActiveAsync(Guid connectionId) => QueryDbAsync(db =>
        db.Connections.IgnoreQueryFilters().Where(c => c.Id == connectionId).Select(c => c.IsActive).SingleAsync());

    private Task<List<bool>> AccountActiveStatesAsync(Guid connectionId) => QueryDbAsync(db =>
        db.Accounts.IgnoreQueryFilters().Where(a => a.ConnectionId == connectionId).Select(a => a.IsActive).ToListAsync());

    private Task<bool> OrderExistsAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().AnyAsync(o => o.Id == orderId));

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

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
