using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when a real Cohere API key is present in the environment (gh#976,
/// of gh#975) — mirroring <see cref="Staging.StagingVenueFactAttribute"/>'s shape for a third-party credential
/// rather than a staging deployment. Otherwise it sets <see cref="FactAttribute.Skip"/>, so it never runs pre-merge
/// without the secret and its absence is reported, not silently green.
/// </summary>
/// <remarks>
/// This environment (and, as of gh#976, the pre-merge CI leg) carries no Cohere credential — the rerank card is
/// operator-gated and keyless-degrade is half its point — so this case is expected to <b>skip</b> here; it exists
/// so a deployment that DOES export <see cref="KeyEnvironmentVariable"/> (a future CI secret, or a developer's own
/// shell) gets a real, live assertion of the <c>/v2/rerank</c> contract rather than a permanent no-op.
/// </remarks>
public sealed class CohereLiveFactAttribute : FactAttribute
{
    /// <summary>The environment variable a real key is read from.</summary>
    public const string KeyEnvironmentVariable = "COHERE_API_KEY";

    /// <summary>Skips unless <see cref="KeyEnvironmentVariable"/> is set.</summary>
    public CohereLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyEnvironmentVariable)))
        {
            Skip = $"blocked: no live Cohere credential — set {KeyEnvironmentVariable} to exercise the real /v2/rerank contract (gh#976)";
        }
    }
}
