using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// Reads Finnhub's general market news <b>off the wire</b>, independently of <c>FinnhubNewsSource</c> — the
/// suite's own eyes on what the provider actually served (gh#1122).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The obvious way to know what a live poll should have stored is to ask the
/// registered <c>INewsSource</c>. That is worthless as an oracle: the adapter is the thing under test, so a
/// defect in it moves the expectation and the outcome by the same amount and every assertion still passes. It is
/// not a hypothetical — an inverted lookback filter and a silent drop of blank-summary articles were both
/// injected into <c>FinnhubNewsSource</c> and the adapter-derived assertions stayed green through both. Only an
/// expectation computed from the provider's own payload can fail on an adapter defect.
/// </para>
/// <para>
/// It deliberately reimplements the request — the documented endpoint, the <c>X-Finnhub-Token</c> header, and
/// the raw field names (<c>url</c>, <c>headline</c>, <c>datetime</c> as Unix seconds) — rather than sharing any
/// code with the adapter. Shared parsing would reintroduce exactly the blind spot it exists to remove.
/// </para>
/// </remarks>
internal static class FinnhubWireProbe
{
    private const string NewsEndpoint = "https://finnhub.io/api/v1/news?category=general";

    /// <summary>One article as the provider serialised it, before any adapter touched it.</summary>
    /// <param name="Url">The story URL — the dedup identity.</param>
    /// <param name="Headline">The headline.</param>
    /// <param name="PublishedAt">Publication time, decoded from the provider's Unix-epoch seconds.</param>
    internal sealed record WireArticle(string Url, string Headline, DateTimeOffset PublishedAt);

    /// <summary>Fetches the provider's current general-news payload.</summary>
    /// <param name="apiKey">The Finnhub token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every article the provider served, unfiltered.</returns>
    public static async Task<IReadOnlyList<WireArticle>> FetchAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.Add("X-Finnhub-Token", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await client.GetAsync(NewsEndpoint, cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonElement[] payload =
            await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken) ?? [];

        List<WireArticle> articles = [];
        foreach (JsonElement element in payload)
        {
            string url = Text(element, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue; // no URL, no identity — it could never be stored under any implementation
            }

            long epochSeconds = element.TryGetProperty("datetime", out JsonElement stamp)
                && stamp.ValueKind == JsonValueKind.Number
                    ? stamp.GetInt64()
                    : 0;

            articles.Add(new WireArticle(
                url,
                Text(element, "headline"),
                DateTimeOffset.FromUnixTimeSeconds(epochSeconds)));
        }

        return articles;
    }

    private static readonly HashSet<string> _trackingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "mc_cid", "mc_eid", "igshid", "ref", "ref_src",
    };

    /// <summary>
    /// Normalises a URL for comparison — independently reimplemented, deliberately <b>not</b> a call into
    /// <c>NewsDedupKey.For</c> (that would make the probe share the very canonicalization it exists to check,
    /// so a defect in it would move probe and production together and the probe could never catch it). This is
    /// a separate implementation reaching for the same tolerances by its own route: lower-cases the host, drops
    /// the scheme and a leading <c>www.</c>, strips a fixed set of tracking query parameters and sorts what's
    /// left, and trims a trailing slash. Weaker than that (case + trailing slash alone) risks reporting a
    /// "dropped story" on a URL production would actually have deduped correctly.
    /// </summary>
    /// <param name="url">The URL to normalise.</param>
    /// <returns>The comparable form.</returns>
    public static string Normalize(string url)
    {
        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return trimmed.TrimEnd('/').ToLowerInvariant();
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host["www.".Length..];
        }

        string path = uri.AbsolutePath.TrimEnd('/');

        string[] queryParts = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                && !_trackingKeys.Contains(pair.Split('=', 2)[0]))
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToArray();

        string query = queryParts.Length == 0 ? string.Empty : "?" + string.Join('&', queryParts);
        return $"{host}{path}{query}";
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
