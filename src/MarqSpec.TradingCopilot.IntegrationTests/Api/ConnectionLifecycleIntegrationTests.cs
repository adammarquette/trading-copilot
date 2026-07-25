using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;

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
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

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
}
