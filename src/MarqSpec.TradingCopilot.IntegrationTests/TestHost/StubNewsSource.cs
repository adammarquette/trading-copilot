using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An adversarial <see cref="INewsSource"/> for the gh#360 news-ingestion suite. It stands in for a real provider
/// (Finnhub / Tiingo, gh#383 — since landed) so the ingestion + dedup engine can be exercised venue-independently
/// in the pre-merge tier, without the real APIs, rate limits, or network flakiness on the PR-feedback path.
/// <para>
/// Those providers are now exercised for real by the live-provider tier
/// (<c>MarketData/LiveNewsProviderIntegrationTests</c>, gh#1122), which does not supersede this stub: it stays the
/// only way to CONSTRUCT input a live feed does not happen to serve — which is exactly why the same-feed
/// fuzzy-dedup case (gh#1128) belongs here rather than there.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>It feeds inputs, never the production-computed answer</b> (QA contract). It returns raw
/// <see cref="NewsItem"/>s with raw URLs and lets <c>NewsIngestionService</c> compute the dedup key and the
/// provenance feeds — a stub that pre-canonicalised URLs or pre-assigned feeds could not catch a dedup that only
/// matches exact strings, which is the regression this suite exists to guard.
/// </para>
/// <para>
/// <see cref="Items"/> is mutable and <see cref="LastSince"/> is recorded, so a test can change what a source
/// returns between passes (provenance-union-on-re-poll) and prove the source was actually invoked.
/// </para>
/// </remarks>
public sealed class StubNewsSource : INewsSource
{
    /// <summary>Creates a stub feed under <paramref name="feedKey"/> (its <see cref="VenueId"/>, used as the provenance feed name).</summary>
    /// <param name="feedKey">The feed identifier, e.g. "finnhub".</param>
    public StubNewsSource(string feedKey)
    {
        Id = VenueId.Parse(feedKey);
    }

    /// <inheritdoc />
    public VenueId Id { get; }

    /// <inheritdoc />
    public VenueCapabilities Capabilities => VenueCapabilities.None;

    /// <inheritdoc />
    public int AdapterLogicVersion => 1;

    /// <summary>What this source returns on the next fetch. Mutable so a test can vary it between passes.</summary>
    public IReadOnlyList<NewsItem> Items { get; set; } = [];

    /// <summary>When set, the next fetch throws it — models a rate-limited / erroring provider.</summary>
    public Exception? ThrowOnFetch { get; set; }

    /// <summary>The <c>since</c> argument of the most recent fetch, or null if never fetched.</summary>
    public DateTimeOffset? LastSince { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<NewsItem>> GetNewsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        LastSince = since;

        if (ThrowOnFetch is not null)
        {
            throw ThrowOnFetch;
        }

        return Task.FromResult(Items);
    }
}
