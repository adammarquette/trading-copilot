using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests verifying the operator's end-to-end firm onboarding, connection registration,
/// account discovery, stage resolution, and fail-closed safety gates (PRD R-14, R-17, gh#9, gh#60, gh#76).
/// Runs against real PostgreSQL via <see cref="PostgresApiFactory"/> (gh#121).
/// </summary>
public class FirmOnboardingEndpointsIntegrationTests : IClassFixture<FirmOnboardingPostgresFactory>
{
    private readonly FirmOnboardingPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record MeUserResponse(Guid Id, string Email, string Role);

    public FirmOnboardingEndpointsIntegrationTests(FirmOnboardingPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OnboardingWorkflow_ShouldAuthenticate_RegisterFirm_DeclareConventions_DiscoverAccounts_AndManageOverrides()
    {
        HttpClient client = _factory.CreateClient();

        // 1. Authenticate operator
        using HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? auth = await loginResponse.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        // Verify /auth/me
        using HttpResponseMessage meResponse = await client.GetAsync("/auth/me");
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        MeUserResponse? user = await meResponse.Content.ReadFromJsonAsync<MeUserResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(user);
        user.Email.Should().Be(PostgresApiFactory.OperatorEmail);

        // 2. Register Firm (PropFirm)
        using HttpResponseMessage createFirmResponse = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep", FirmType.PropFirm));
        createFirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        FirmResponse? firm = await createFirmResponse.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        // 3. Declare Firm Conventions (Practice & Evaluation = Capital Not At Risk [Practice], Funded = Capital At Risk [Live])
        using HttpResponseMessage conventionsResponse = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest(
            [
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true),
            ]));
        conventionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Create Connection
        using HttpResponseMessage createConnResponse = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        createConnResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ConnectionResponse? connection = await createConnResponse.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        // 5. Discover Accounts (Adversarial venue stub claims ALL accounts are Live; API domain logic must override)
        using HttpResponseMessage discoverResponse = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<AccountResponse>? discovered = await discoverResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(discovered);
        discovered.Should().HaveCount(4);

        AccountResponse pracAccount = discovered.First(a => a.VenueAccountKey == "PRAC-50K-101");
        pracAccount.Stage.Should().Be(AccountStage.Practice);
        pracAccount.Mode.Should().Be(TradingMode.Practice); // Proves API recomputed mode under conventions, ignoring venue's claim
        pracAccount.TradeableHere.Should().BeTrue();

        AccountResponse evalAccount = discovered.First(a => a.VenueAccountKey == "50KTC-V2-202");
        evalAccount.Stage.Should().Be(AccountStage.Evaluation);
        evalAccount.Mode.Should().Be(TradingMode.Practice);
        evalAccount.TradeableHere.Should().BeTrue();

        AccountResponse fundedAccount = discovered.First(a => a.VenueAccountKey == "EXPRESS-50K-303");
        fundedAccount.Stage.Should().Be(AccountStage.Funded);
        fundedAccount.Mode.Should().Be(TradingMode.Live);
        fundedAccount.TradeableHere.Should().BeFalse(); // Development environment gate!

        // 6. Set Stage Override on Evaluation Account -> Funded
        using HttpResponseMessage setOverrideResponse = await client.PutAsJsonAsync(
            $"/accounts/{evalAccount.Id}/stage",
            new SetStageOverrideRequest(AccountStage.Funded));
        setOverrideResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AccountResponse? overridden = await setOverrideResponse.Content.ReadFromJsonAsync<AccountResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(overridden);
        overridden.StageOverride.Should().Be(AccountStage.Funded);
        overridden.Mode.Should().Be(TradingMode.Live);
        overridden.TradeableHere.Should().BeFalse();

        // 7. Clear Stage Override -> Falls back to Evaluation -> Practice mode
        using HttpResponseMessage clearOverrideResponse = await client.DeleteAsync($"/accounts/{evalAccount.Id}/stage");
        clearOverrideResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AccountResponse? cleared = await clearOverrideResponse.Content.ReadFromJsonAsync<AccountResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(cleared);
        cleared.StageOverride.Should().BeNull();
        cleared.Stage.Should().Be(AccountStage.Evaluation);
        cleared.Mode.Should().Be(TradingMode.Practice);
        cleared.TradeableHere.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldResolveUnclassifiableName_ToUnknownStage_AndUndeclaredMode_FailClosed()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FirmResponse firm = await CreateFirmAsync(client, "Topstep-Unclassifiable");
        ConnectionResponse connection = await CreateConnectionAsync(client, firm.Id, "topstep-main");

        using HttpResponseMessage discoverResponse = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<AccountResponse>? discovered = await discoverResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(discovered);

        AccountResponse unclassifiable = discovered.First(a => a.VenueAccountKey == "UNKNOWN-NAME-999");
        unclassifiable.Stage.Should().Be(AccountStage.Unknown);
        unclassifiable.Mode.Should().Be(TradingMode.Undeclared);
        unclassifiable.TradeableHere.Should().BeFalse();
    }

    [Fact]
    public async Task SetStageOverride_ShouldRefuseUnknownStage_WithBadRequest()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FirmResponse firm = await CreateFirmAsync(client, "Topstep-BadRequest");
        ConnectionResponse connection = await CreateConnectionAsync(client, firm.Id, "topstep-main");

        using HttpResponseMessage discoverResponse = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        List<AccountResponse>? discovered = await discoverResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(discovered);
        AccountResponse evalAccount = discovered.First(a => a.VenueAccountKey == "50KTC-V2-202");

        // Attempt setting override to Unknown -> MUST be refused as 400 BadRequest
        using HttpResponseMessage badOverrideResponse = await client.PutAsJsonAsync(
            $"/accounts/{evalAccount.Id}/stage",
            new SetStageOverrideRequest(AccountStage.Unknown));
        badOverrideResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldPreserveExistingStageOverride_UponRediscovery()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FirmResponse firm = await CreateFirmAsync(client, "Topstep-Rediscovery");
        await DeclareConventionsAsync(client, firm.Id);
        ConnectionResponse connection = await CreateConnectionAsync(client, firm.Id, "topstep-main");

        // 1. Initial discovery
        using HttpResponseMessage firstDiscover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        List<AccountResponse>? discovered = await firstDiscover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(discovered);
        AccountResponse evalAccount = discovered.First(a => a.VenueAccountKey == "50KTC-V2-202");

        // 2. Set Stage Override to Funded
        await client.PutAsJsonAsync($"/accounts/{evalAccount.Id}/stage", new SetStageOverrideRequest(AccountStage.Funded));

        // 3. Rediscover accounts -> StageOverride MUST be preserved
        using HttpResponseMessage secondDiscover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        List<AccountResponse>? rediscovered = await secondDiscover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(rediscovered);

        AccountResponse preservedAccount = rediscovered.First(a => a.VenueAccountKey == "50KTC-V2-202");
        preservedAccount.StageOverride.Should().Be(AccountStage.Funded);
        preservedAccount.Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldRefuse_WhenCredentialKeyMismatchesProcessConfig()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FirmResponse firm = await CreateFirmAsync(client, "Topstep-MismatchKey");
        // Create connection with credential key mismatch
        using HttpResponseMessage createConnResponse = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "other-key"));
        ConnectionResponse? connection = await createConnResponse.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        // Discovery on mismatched key connection MUST fail with 409 Conflict (ADR-0015)
        using HttpResponseMessage discoverResponse = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Development, false)]
    [InlineData(DeploymentEnvironment.Staging, false)]
    [InlineData(DeploymentEnvironment.Production, true)]
    public async Task EnvironmentGate_ShouldEnforceTradeableHereRules_AcrossEnvironments(DeploymentEnvironment environment, bool expectedFundedTradeable)
    {
        using WebApplicationFactory<Program> envHost = _factory.CreateHost(environment);
        HttpClient client = envHost.CreateClient();

        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FirmResponse firm = await CreateFirmAsync(client, $"Topstep-Env-{environment}");
        await client.PutAsJsonAsync($"/firms/{firm.Id}/conventions", new DeclareConventionsRequest(
        [
            new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true)
        ]));
        ConnectionResponse connection = await CreateConnectionAsync(client, firm.Id, "topstep-main");

        using HttpResponseMessage discoverResponse = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        List<AccountResponse>? discovered = await discoverResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(discovered);

        AccountResponse fundedAccount = discovered.First(a => a.VenueAccountKey == "EXPRESS-50K-303");
        fundedAccount.Mode.Should().Be(TradingMode.Live);
        fundedAccount.TradeableHere.Should().Be(expectedFundedTradeable);
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private async Task<FirmResponse> CreateFirmAsync(HttpClient client, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(name, FirmType.PropFirm));
        FirmResponse? firm = await response.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);
        return firm;
    }

    private async Task<ConnectionResponse> CreateConnectionAsync(HttpClient client, Guid firmId, string credentialKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firmId, "projectx", credentialKey));
        ConnectionResponse? connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);
        return connection;
    }

    private async Task DeclareConventionsAsync(HttpClient client, Guid firmId)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync($"/firms/{firmId}/conventions", new DeclareConventionsRequest(
        [
            new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true)
        ]));
        response.EnsureSuccessStatusCode();
    }
}
