namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Cohere embedding configuration (gh#403). The key is server-side only, from the environment — never in source.
/// An unset key disables the provider cleanly (<see cref="IsConfigured"/> is false), so the keyless
/// <c>UnavailableEmbeddingProvider</c> stays in place and nothing crashes at first use.
/// </summary>
public sealed class CohereOptions
{
    /// <summary>The configuration section (<c>Cohere</c>).</summary>
    public const string SectionName = "Cohere";

    /// <summary>The API key. Unset (null / blank) disables the provider.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The embedding model. Its output width must equal the pgvector column dimension (1024).</summary>
    public string Model { get; init; } = "embed-english-v3.0";

    /// <summary>
    /// The rerank model (gh#975). Cohere's cross-encoder reranker; distinct from the embed <see cref="Model"/> above,
    /// so the two seams tune independently. Non-secret, so an operator can retune it without a code change.
    /// </summary>
    public string RerankModel { get; init; } = "rerank-english-v3.0";

    /// <summary>The API base address.</summary>
    public string BaseUrl { get; init; } = "https://api.cohere.com";

    /// <summary>
    /// The embed cost estimate, pinned in <b>one place</b> (gh#403 acceptance): USD per million billed input tokens.
    /// Cohere's <c>embed-english-v3.0</c> free/standard rate at the time of writing. A price change is this one
    /// line — or an environment override — not a hunt through the code.
    /// </summary>
    public decimal UsdPerMillionTokens { get; init; } = 0.10m;

    /// <summary>
    /// The rerank cost estimate, pinned in <b>one place</b> (gh#975): USD per <b>thousand searches</b>. Cohere rerank
    /// bills per <i>search</i> — one call is one search unit (up to its per-call document cap) — <b>not</b> per token,
    /// so it is priced on its own rate, never <see cref="UsdPerMillionTokens"/>. Cohere's <c>rerank-english-v3.0</c>
    /// rate at the time of writing; a price change is this one line, or an environment override.
    /// </summary>
    public decimal UsdPerThousandSearches { get; init; } = 2.00m;

    /// <summary>Whether a usable key is present.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Estimated USD cost of <paramref name="billedTokens"/> at the configured embed rate.</summary>
    /// <param name="billedTokens">Tokens billed by the provider.</param>
    /// <returns>The estimated dollar cost.</returns>
    public decimal EstimateCost(int billedTokens) =>
        billedTokens <= 0 ? 0m : billedTokens / 1_000_000m * UsdPerMillionTokens;

    /// <summary>Estimated USD cost of <paramref name="billedSearches"/> at the configured per-search rerank rate.</summary>
    /// <param name="billedSearches">Search units billed by the provider.</param>
    /// <returns>The estimated dollar cost.</returns>
    public decimal EstimateRerankCost(int billedSearches) =>
        billedSearches <= 0 ? 0m : billedSearches / 1_000m * UsdPerThousandSearches;
}
