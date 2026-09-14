using System.Net;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// Asks Tiingo two questions the adapter cannot separate: <b>is this token valid</b>, and <b>is this plan
/// entitled to news</b> (gh#1122, gh#1125).
/// </summary>
/// <remarks>
/// The adapter surfaces an <c>HttpRequestException</c> carrying only a status code, and Tiingo answers
/// <c>403</c> to <i>both</i> an unissued token ("Invalid token.") and a valid token whose plan excludes the News
/// API ("You do not have permission to access the News API"). A pin that asserts "403" therefore cannot tell the
/// finding it claims to record from a dead credential — and would keep passing, still citing gh#1125, if the
/// operator's token were simply revoked. Reading the response <b>body</b>, and separately confirming the token
/// authenticates at all against an endpoint every plan carries, is what makes the distinction observable.
/// </remarks>
internal static class TiingoWireProbe
{
    private const string TestEndpoint = "https://api.tiingo.com/api/test";
    private const string NewsEndpoint = "https://api.tiingo.com/tiingo/news?startDate=2020-01-01";

    /// <summary>The outcome of one probe: the status the API answered and the body it returned.</summary>
    /// <param name="Status">The HTTP status.</param>
    /// <param name="Body">The response body, verbatim.</param>
    internal sealed record ProbeResult(HttpStatusCode Status, string Body);

    /// <summary>Calls an endpoint every Tiingo plan carries — a 200 means the token itself authenticates.</summary>
    /// <param name="apiToken">The Tiingo token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status and body.</returns>
    public static Task<ProbeResult> ProbeTokenAsync(string apiToken, CancellationToken cancellationToken = default) =>
        SendAsync(TestEndpoint, apiToken, cancellationToken);

    /// <summary>Calls the news endpoint — the one the plan may or may not include.</summary>
    /// <param name="apiToken">The Tiingo token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status and body.</returns>
    public static Task<ProbeResult> ProbeNewsAsync(string apiToken, CancellationToken cancellationToken = default) =>
        SendAsync(NewsEndpoint, apiToken, cancellationToken);

    private static async Task<ProbeResult> SendAsync(
        string endpoint,
        string apiToken,
        CancellationToken cancellationToken)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Add("Authorization", $"Token {apiToken}");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ProbeResult(response.StatusCode, body);
    }
}
