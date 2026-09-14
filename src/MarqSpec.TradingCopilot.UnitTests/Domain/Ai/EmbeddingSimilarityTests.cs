using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Ai;

/// <summary>
/// The pure semantic-similarity math (gh#853): a candidate embedding's <b>max</b> cosine similarity to the operator's
/// starred set — the nearest single star, clamped to <c>[0, 1]</c>. This is where the salience ranking is proven; the
/// vector <i>read</i> is integration-tier, the ranking is a deterministic function of its inputs.
/// </summary>
public class EmbeddingSimilarityTests
{
    private static IReadOnlyList<float> V(params float[] values) => values;

    [Fact]
    public void MaxCosineSimilarity_IsOne_ForAnIdenticalDirection()
    {
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(1f, 0f)]).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void MaxCosineSimilarity_IsMagnitudeInvariant()
    {
        // Cosine measures direction, not length: a scaled star is the same direction -> still identical.
        EmbeddingSimilarity.MaxCosineSimilarity(V(2f, 0f), [V(5f, 0f)]).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void MaxCosineSimilarity_IsZero_ForAnOrthogonalVector()
    {
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(0f, 1f)]).Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void MaxCosineSimilarity_ClampsToZero_ForAnOppositeVector()
    {
        // Raw cosine is -1; the axis is a [0, 1] similarity, so an opposite vector reads as no signal, not a negative.
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(-1f, 0f)]).Should().Be(0.0);
    }

    [Fact]
    public void MaxCosineSimilarity_TakesTheNearestStar_NotTheMean()
    {
        // Near one reference, orthogonal to another: the MAX (nearest single star) is 1, not the mean 0.5.
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(0f, 1f), V(1f, 0f)])
            .Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void MaxCosineSimilarity_ReflectsPartialAngle_BetweenZeroAndOne()
    {
        // A 45-degree separation is cos 45 ~= 0.707 -- a partial similarity between orthogonal (0) and identical (1).
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 1f), [V(1f, 0f)])
            .Should().BeApproximately(1.0 / Math.Sqrt(2.0), 1e-6);
    }

    [Fact]
    public void MaxCosineSimilarity_IsZero_WhenThereAreNoReferences()
    {
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), []).Should().Be(0.0);
    }

    [Fact]
    public void MaxCosineSimilarity_IsZero_ForAZeroMagnitudeVector()
    {
        // A zero vector has no direction: cosine is undefined -> 0 (no signal), never a divide-by-zero.
        EmbeddingSimilarity.MaxCosineSimilarity(V(0f, 0f), [V(1f, 0f)]).Should().Be(0.0);
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(0f, 0f)]).Should().Be(0.0);
    }

    [Fact]
    public void MaxCosineSimilarity_IsZero_OnADimensionMismatch()
    {
        // Vectors from different models have different widths and no shared angle -- no signal, never a throw.
        EmbeddingSimilarity.MaxCosineSimilarity(V(1f, 0f), [V(1f, 0f, 0f)]).Should().Be(0.0);
    }
}
