using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The semantic-embedding salience axis (gh#853, R-2, R-9): scores each of the feed's <b>window candidates</b> by the
/// <b>maximum cosine similarity</b> between its stored embedding and the operator's <b>starred</b> items' embeddings
/// (the nearest single star, not the mean), returning a per-candidate <c>dedupKey → similarity</c> map in
/// <c>[0, 1]</c> — the operator-relative axis <see cref="Domain.Signals.SalienceScorer"/> folds into an item's
/// multiplier. It reads both sets' stored vectors over the <see cref="INewsEmbeddingSimilarity"/> seam and ranks them
/// with the pure <see cref="Domain.Ai.EmbeddingSimilarity"/> helper, so it scores the KNOWN candidates rather than
/// searching for a global nearest set that could miss a recent-but-near item.
/// </summary>
/// <remarks>
/// <para>
/// <b>Degrade, never throw</b> — the gh#109 posture the news-embedding read seam's consumers share. Several
/// independent things leave the axis off on a healthy deployment: no embedding provider (so <c>IsAvailable</c> is
/// false), nothing starred, no candidates, no starred item embedded yet, or a <c>pgvector</c> read that faults. Each
/// yields an empty map so the feed keeps working on its categorical axes; none surfaces as an error. Only a genuine
/// caller cancellation propagates; a downstream timeout on an internal token degrades like any other read fault (gh#589).
/// </para>
/// <para>
/// <b>No read when there is nothing to rank.</b> An unavailable provider, an empty starred set, or an empty candidate
/// window short-circuits <i>before</i> the seam call, so the axis never reads when it could only return noise.
/// </para>
/// <para>
/// <b>Scoped.</b> It holds the <see cref="INewsEmbeddingSimilarity"/> seam, whose production implementation carries
/// the scoped <c>TradingCopilotDbContext</c>, so this is a scoped service too.
/// </para>
/// </remarks>
public sealed class SemanticSalienceAxis
{
    private static readonly IReadOnlyDictionary<string, double> _empty =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private readonly IEmbeddingProvider _provider;
    private readonly INewsEmbeddingSimilarity _similarity;
    private readonly ILogger<SemanticSalienceAxis> _logger;

    /// <summary>Creates the axis over the embedding provider (the availability gate) and the similarity seam.</summary>
    /// <param name="provider">The embedding provider — its <see cref="IEmbeddingProvider.IsAvailable"/> gates the read.</param>
    /// <param name="similarity">The nearest-to-owners seam — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="logger">The logger.</param>
    public SemanticSalienceAxis(
        IEmbeddingProvider provider,
        INewsEmbeddingSimilarity similarity,
        ILogger<SemanticSalienceAxis> logger)
    {
        _provider = provider;
        _similarity = similarity;
        _logger = logger;
    }

    /// <summary>Scores each window candidate's semantic nearness to the operator's stars, as a <c>dedupKey → similarity</c> map.</summary>
    /// <param name="candidateKeys">The feed's window candidates — the items to score (their dedup keys).</param>
    /// <param name="starredKeys">The operator's starred item keys — the reference set each candidate is scored against.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Candidate dedup key → max cosine similarity in <c>[0, 1]</c>; empty when the axis is unavailable, unstarred, candidate-less, or faulted.</returns>
    public async Task<IReadOnlyDictionary<string, double>> ForCandidatesAsync(
        IReadOnlyCollection<string> candidateKeys,
        IReadOnlyCollection<string> starredKeys,
        CancellationToken cancellationToken)
    {
        if (!_provider.IsAvailable || starredKeys.Count == 0 || candidateKeys.Count == 0)
        {
            // No provider (the gh#109 degrade), nothing starred, or no candidates: there is nothing to rank, so the
            // axis is simply off -- no seam read, and the caller falls back to its categorical axes.
            return _empty;
        }

        // A starred item already carries its star; the semantic axis scores OTHER candidates' nearness to the starred
        // set, so drop any candidate that is itself starred (it would trivially self-match at similarity 1).
        HashSet<string> starred = new(starredKeys, StringComparer.Ordinal);
        List<string> scorableKeys = [.. candidateKeys.Where(key => !starred.Contains(key))];
        if (scorableKeys.Count == 0)
        {
            return _empty;
        }

        try
        {
            // Two plain by-owner reads: the reference (starred) vectors, then the scorable candidates' vectors -- a
            // bounded, constant number of reads (SF3), not one HNSW query per star.
            IReadOnlyList<StoredEmbedding> references = await _similarity.GetVectorsAsync(starredKeys, cancellationToken);
            if (references.Count == 0)
            {
                // Nothing starred is embedded yet: no reference vectors to rank against.
                return _empty;
            }

            IReadOnlyList<StoredEmbedding> candidates = await _similarity.GetVectorsAsync(scorableKeys, cancellationToken);

            List<IReadOnlyList<float>> referenceVectors = [.. references.Select(embedding => embedding.Vector)];
            Dictionary<string, double> map = new(StringComparer.Ordinal);
            foreach (StoredEmbedding candidate in candidates)
            {
                // Max cosine similarity to the nearest single star, clamped to [0, 1] by the pure helper; the scorer
                // only counts a strictly-positive similarity, so a dissimilar candidate (0) contributes nothing.
                map[candidate.OwnerId] = EmbeddingSimilarity.MaxCosineSimilarity(candidate.Vector, referenceVectors);
            }

            return map;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation is host shutdown, not a read fault to swallow
        }
        catch (Exception error)
        {
            // The read is off the trading path: a pgvector outage (or any read fault) degrades the feed to its
            // categorical axes rather than erroring.
            _logger.LogWarning(
                error, "Semantic salience axis failed; the feed degrades to its categorical axes for this read.");
            return _empty;
        }
    }
}
