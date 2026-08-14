namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// Turns text into an embedding vector (gh#109, engineering §2) — the seam a real provider (Cohere, gh#403)
/// implements and everything downstream depends on instead of an SDK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unavailable is a first-class answer, not an exception.</b> Embedding depends on an external, rate-limited,
/// paid API and on a Postgres extension that may not exist. Every one of those can be absent on a perfectly
/// healthy deployment, so the seam reports it rather than throwing — and a caller that ignores
/// <see cref="IsAvailable"/> gets a <see langword="null"/> <see cref="EmbeddingResult.Vector"/> rather than a
/// plausible wrong vector.
/// </para>
/// <para>
/// <b>Why null rather than an empty vector.</b> A zero-length or zero-filled vector is a <i>valid</i> input to a
/// similarity search: it returns results, all equally meaningless. Retrieval that silently degrades to noise is
/// worse than retrieval that says it cannot run — the same reasoning that makes an unreachable venue
/// declared-unknown rather than an empty book (gh#381).
/// </para>
/// <para>
/// <b>The result carries spend facts, not just the vector</b> (gh#377) — mirroring how <see cref="ILlmProvider"/>
/// rides <see cref="LlmUsage"/> on its <see cref="LlmCompletion"/>. The embed pass ledgers an honest
/// <c>AIUsage</c> row per call (gh#436): <see cref="EmbeddingResult.Outcome"/>,
/// <see cref="EmbeddingResult.BilledTokens"/> and <see cref="EmbeddingResult.EstimatedCostUsd"/> are priced <b>at
/// the provider</b> — the same values <see cref="IEmbeddingMetrics"/> meters — and ride back so a scoped consumer
/// can price and record the call without knowing which concrete provider (or rate) is behind the seam. The seam
/// would otherwise force a choice between fabricating a cost and recording none, and neither is acceptable.
/// </para>
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>The model identifier stored alongside each vector — vectors from different models are not comparable.</summary>
    string Model { get; }

    /// <summary>The vector width this provider emits. Must match the stored column, or nothing round-trips.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Whether embedding can currently be performed. <see langword="false"/> is an ordinary state — no API key,
    /// no pgvector, a provider deliberately disabled — and callers must branch on it rather than discovering it
    /// through a failure.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Embeds <paramref name="text"/> as a stored <b>document</b> — the write path (gh#377).</summary>
    /// <param name="text">The content to embed.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The result, whose <see cref="EmbeddingResult.Vector"/> is null when embedding is unavailable or failed.</returns>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken);

    /// <summary>Embeds <paramref name="text"/> as a retrieval <b>query</b> (gh#852).</summary>
    /// <remarks>
    /// A query and the documents it should match land in a shared space only when each side declares its role: the
    /// query is embedded as <c>search_query</c> here, the write path (<see cref="EmbedAsync"/>) as
    /// <c>search_document</c>. Embedding a query as a document silently degrades match quality — hence the separate
    /// method rather than a shared default. The same contract as <see cref="EmbedAsync"/> otherwise holds: the
    /// <see cref="EmbeddingResult.Vector"/> is <see langword="null"/> when embedding is unavailable or failed, never
    /// a zero vector, and only a genuine caller cancellation propagates.
    /// </remarks>
    /// <param name="text">The query text to embed.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The result, whose <see cref="EmbeddingResult.Vector"/> is null when embedding is unavailable or failed.</returns>
    Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// The result of one embed call (gh#377) — mirrors <see cref="LlmCompletion"/> for the embedding seam: the vector a
/// retrieval caller wants, plus the spend facts a scoped consumer needs to ledger the call (gh#436, ADR-0008)
/// without reaching into a concrete provider's pricing.
/// </summary>
/// <param name="Vector">The embedding, or <see langword="null"/> when the call did not produce one.</param>
/// <param name="Outcome">How the call ended — the same dimension <see cref="IEmbeddingMetrics"/> meters.</param>
/// <param name="BilledTokens">Tokens the provider billed; zero for a failed, rate-limited, or unattempted call.</param>
/// <param name="EstimatedCostUsd">
/// The dollar cost of <paramref name="BilledTokens"/>, priced at the provider's own pinned rate; zero when none
/// were billed.
/// </param>
public sealed record EmbeddingResult(
    IReadOnlyList<float>? Vector, EmbeddingOutcome Outcome, int BilledTokens, decimal EstimatedCostUsd);
