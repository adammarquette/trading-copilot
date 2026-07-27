using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Builds an authenticated <see cref="HttpClient"/> against the <b>deployed</b> staging API — base URL and operator
/// credentials from <see cref="StagingConfig"/> (CI secrets). Unlike the pre-merge factories this starts no
/// container and stubs no venue: the target is a real environment reached over the network, so callers must be
/// gated behind a <c>Staging*Fact</c> (the config is guaranteed present only there).
/// </summary>
internal static class StagingApiClient
{
    private sealed record LoginTokenResponse(string Token);

    /// <summary>Logs in as the staging operator and returns a bearer-authenticated client rooted at the staging API.</summary>
    public static async Task<HttpClient> AuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        HttpClient client = new() { BaseAddress = new Uri(StagingConfig.ApiBaseUrl!) };
        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/auth/login",
                new LoginRequest(StagingConfig.OperatorEmail!, StagingConfig.OperatorPassword!),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(
                JsonSerializerOptions.Web, cancellationToken);
            ArgumentNullException.ThrowIfNull(auth);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
