using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Shared setup for the staging <b>venue-execution</b> gates (gh#269, gh#293): authenticate against the deployed API
/// and resolve the <b>reserved ProjectX practice account</b> (<c>STAGING_PROJECTX_PRACTICE_ACCOUNT</c>). Concrete
/// suites join <see cref="StagingExecutionCollection"/> so their order-placing runs are serialized onto that one
/// account. PRACTICE ONLY (R-14) — a live account is never wired here.
/// </summary>
public abstract class StagingVenueExecutionSuite
{
    /// <summary>An authenticated client against the deployed staging API.</summary>
    protected static Task<HttpClient> AuthenticatedClientAsync(CancellationToken cancellationToken = default) =>
        StagingApiClient.AuthenticatedAsync(cancellationToken);

    /// <summary>
    /// Resolves the reserved practice account id, creating the firm/connection on first run and reusing them after
    /// (staging is persistent, so setup is idempotent). Returns the account whose venue key matches the reserved one.
    /// </summary>
    protected static async Task<Guid> ResolvePracticeAccountAsync(HttpClient client)
    {
        Guid connectionId = await EnsureConnectionAsync(client);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connectionId}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.SingleOrDefault(a => a.VenueAccountKey == StagingConfig.PracticeAccountKey)
            ?? throw new InvalidOperationException(
                $"The reserved practice account '{StagingConfig.PracticeAccountKey}' was not discovered on the staging connection.");
        return account.Id;
    }

    private static async Task<Guid> EnsureConnectionAsync(HttpClient client)
    {
        using HttpResponseMessage list = await client.GetAsync("/connections");
        list.EnsureSuccessStatusCode();
        List<ConnectionResponse>? connections = await list.Content.ReadFromJsonAsync<List<ConnectionResponse>>(JsonSerializerOptions.Web);
        ConnectionResponse? existing = connections?.FirstOrDefault(c =>
            c.Platform == "projectx" && c.CredentialKey == StagingConfig.VenueCredentialKey);
        if (existing is not null)
        {
            return existing.Id;
        }

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest("Staging-Execution-Gate", FirmType.PropFirm));
        createFirm.EnsureSuccessStatusCode();
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", StagingConfig.VenueCredentialKey!));
        createConn.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Id;
    }
}
