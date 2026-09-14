namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Derives the <b>cross-source dedup key</b> for a news item from its URL — the identity that collapses the same
/// story arriving from Finnhub and Tiingo into one stored item, and makes an overlapping re-poll idempotent (R-2).
/// It is the news analogue of a bar's composite key: the persisted key is the idempotence guard, not a check the
/// writer must remember.
/// </summary>
/// <remarks>
/// Canonicalization is deliberately conservative: drop the scheme and fragment, lower-case the host, drop a leading
/// <c>www.</c>, strip tracking query parameters (<c>utm_*</c>, <c>fbclid</c>, <c>gclid</c>, …), sort the survivors,
/// and trim a trailing slash. Two links to the same article that differ only by tracking noise collapse; genuinely
/// different paths stay distinct. A non-absolute URL falls back to its trimmed, lower-cased self, so even a
/// malformed feed still dedups against itself.
/// </remarks>
public static class NewsDedupKey
{
    private static readonly HashSet<string> _trackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "mc_cid", "mc_eid", "igshid", "ref", "ref_src",
    };

    /// <summary>Derives the canonical dedup key for <paramref name="url"/>.</summary>
    /// <param name="url">The story URL as a provider supplied it.</param>
    /// <returns>The canonical key; never null or empty.</returns>
    /// <exception cref="ArgumentException"><paramref name="url"/> is null, empty, or whitespace.</exception>
    public static string For(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A news item must carry a URL to dedup on.", nameof(url));
        }

        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return trimmed.ToLowerInvariant();
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host["www.".Length..];
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        string query = CanonicalQuery(uri.Query);
        return query.Length == 0 ? host + path : $"{host}{path}?{query}";
    }

    private static string CanonicalQuery(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery) || rawQuery == "?")
        {
            return string.Empty;
        }

        IEnumerable<string> pairs = rawQuery
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsTracking(pair))
            .OrderBy(pair => pair, StringComparer.Ordinal);

        return string.Join('&', pairs);
    }

    private static bool IsTracking(string pair)
    {
        string key = pair.Split('=', 2)[0];
        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || _trackingParameters.Contains(key);
    }
}
