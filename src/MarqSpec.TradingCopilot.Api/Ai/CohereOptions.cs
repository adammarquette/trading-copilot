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

    /// <summary>The API base address.</summary>
    public string BaseUrl { get; init; } = "https://api.cohere.com";

    /// <summary>
    /// The cost estimate, pinned in <b>one place</b> (gh#403 acceptance): USD per million billed input tokens.
    /// Cohere's <c>embed-english-v3.0</c> free/standard rate at the time of writing. A price change is this one
    /// line — or an environment override — not a hunt through the code.
    /// </summary>
    public decimal UsdPerMillionTokens { get; init; } = 0.10m;

    /// <summary>Whether a usable key is present.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Estimated USD cost of <paramref name="billedTokens"/> at the configured rate.</summary>
    /// <param name="billedTokens">Tokens billed by the provider.</param>
    /// <returns>The estimated dollar cost.</returns>
    public decimal EstimateCost(int billedTokens) =>
        billedTokens <= 0 ? 0m : billedTokens / 1_000_000m * UsdPerMillionTokens;
}
