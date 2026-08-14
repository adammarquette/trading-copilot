namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// Pure vector-similarity math for the semantic salience axis (gh#853): a candidate embedding's nearness to the
/// operator's starred set, as the <b>maximum</b> cosine similarity to any starred vector — the nearest single star,
/// not the mean. Kept in the domain and free of any store or <c>Pgvector</c> type, so the ranking is a deterministic,
/// fully unit-tested function of its inputs while only the vector <i>read</i> stays integration-tier.
/// </summary>
public static class EmbeddingSimilarity
{
    /// <summary>
    /// The maximum cosine similarity, in <c>[0, 1]</c>, between <paramref name="candidate"/> and any vector in
    /// <paramref name="references"/> — the candidate's nearness to the nearest single reference (star).
    /// </summary>
    /// <remarks>
    /// Cosine similarity is <c>dot(a, b) / (‖a‖·‖b‖)</c>, naturally in <c>[-1, 1]</c>; it is clamped to <c>[0, 1]</c>
    /// so an obtuse (dissimilar) vector reads as no signal rather than a negative weight — the convention the scorer's
    /// <c>sim &gt; 0</c> guard expects. A zero-magnitude or dimension-mismatched pair contributes nothing (0, never a
    /// divide-by-zero or a throw), and an empty reference set yields 0 — there is nothing to be near.
    /// </remarks>
    /// <param name="candidate">The candidate item's embedding.</param>
    /// <param name="references">The reference (starred) embeddings to measure nearness against.</param>
    /// <returns>The max cosine similarity in <c>[0, 1]</c>; 0 when there is no comparable reference.</returns>
    public static double MaxCosineSimilarity(
        IReadOnlyList<float> candidate, IReadOnlyCollection<IReadOnlyList<float>> references)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
        {
            return 0.0;
        }

        double best = references.Max(reference => CosineSimilarity(candidate, reference));
        return Math.Clamp(best, 0.0, 1.0);
    }

    private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        // A dimension mismatch (vectors from different models) or an empty vector has no meaningful angle -- score it
        // as no similarity rather than throwing, keeping the axis a soft, fail-safe signal.
        if (a.Count != b.Count || a.Count == 0)
        {
            return 0.0;
        }

        double dot = 0.0;
        double normA = 0.0;
        double normB = 0.0;
        for (int i = 0; i < a.Count; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        // A zero-magnitude vector has no direction, so cosine is undefined -- 0 (no signal), never a divide-by-zero.
        if (normA <= 0.0 || normB <= 0.0)
        {
            return 0.0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
