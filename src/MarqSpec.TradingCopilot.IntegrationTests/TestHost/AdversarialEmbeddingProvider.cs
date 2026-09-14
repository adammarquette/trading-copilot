using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An <b>adversarial</b> stand-in for the embedding provider (gh#888), mirroring <see cref="AdversarialLlmProvider"/>:
/// Cohere is an outbound third-party seam that cannot exist pre-merge (no key, no egress), so it is doubled here —
/// at the <b>provider</b>, never at the read/filter logic under test. It feeds a deterministic vector and reports
/// whichever <see cref="Model"/> the test currently has it configured to, and both <see cref="PgVectorNewsSimilarity"/>'s
/// by-owner / nearest reads and <see cref="NewsEmbeddingService"/>'s re-embed candidacy run as real production code
/// against what this double reports — the double never computes a filter decision or a candidacy decision itself.
/// </summary>
/// <remarks>
/// <b><see cref="Model"/> is mutable, on purpose.</b> The gh#888 model-transition self-heal case needs ONE running
/// host to observe an owner embedded under an OLD model, then move the deployment's current model forward and
/// re-embed under the NEW one — which needs the SAME Postgres container (so the transition is genuinely observed,
/// not simulated across two hosts) but two different reported models over the run. A real
/// <c>CohereEmbeddingProvider</c> reads its model from static <c>IOptions&lt;CohereOptions&gt;</c> snapshotted at
/// host build time and cannot move mid-run; this double can.
/// </remarks>
public sealed class AdversarialEmbeddingProvider : IEmbeddingProvider
{
    private readonly Lock _gate = new();
    private readonly List<string> _embedded = [];

    /// <summary>The model identifier this double currently reports — settable per test/phase.</summary>
    public string Model { get; set; } = "adversarial-embed-v1";

    /// <inheritdoc />
    public int Dimensions => TradingCopilotDbContext.EmbeddingDimensions;

    /// <summary>Whether embedding can currently be performed — defaults to available (a key IS configured).</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Every text handed to <see cref="EmbedAsync"/> or <see cref="EmbedQueryAsync"/>, in call order.</summary>
    public IReadOnlyList<string> EmbeddedTexts
    {
        get
        {
            lock (_gate)
            {
                return [.. _embedded];
            }
        }
    }

    /// <inheritdoc />
    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Result(text));

    /// <inheritdoc />
    public Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Result(text));

    /// <summary>Clears recorded calls and restores the default available/model state — call per test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _embedded.Clear();
            Model = "adversarial-embed-v1";
            IsAvailable = true;
        }
    }

    // Feeds a vector, never the filter/candidacy decision: deterministic per TEXT (same text -> same vector,
    // stable across repeated calls in one test), independent of Model -- the point under test is whether the
    // READ correctly scopes by whichever Model this double is reporting AT READ TIME, not whether the vector
    // itself differs by model.
    private EmbeddingResult Result(string text)
    {
        lock (_gate)
        {
            _embedded.Add(text);
        }

        if (!IsAvailable)
        {
            return new EmbeddingResult(null, EmbeddingOutcome.Failed, 0, 0m);
        }

        float[] vector = new float[Dimensions];
        int seed = text.GetHashCode(StringComparison.Ordinal);
        Random random = new(seed);
        double normSquared = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            double sample = (random.NextDouble() * 2) - 1;
            vector[i] = (float)sample;
            normSquared += sample * sample;
        }

        double norm = Math.Sqrt(normSquared);
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return new EmbeddingResult(vector, EmbeddingOutcome.Embedded, BilledTokens: 1, EstimatedCostUsd: 0m);
    }
}
