namespace MarqSpec.TradingCopilot.Domain.AI;

/// <summary>
/// Turns text into an embedding vector (gh#109, engineering §2) — the seam a real provider (Cohere, gh#403)
/// implements and everything downstream depends on instead of an SDK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unavailable is a first-class answer, not an exception.</b> Embedding depends on an external, rate-limited,
/// paid API and on a Postgres extension that may not exist. Every one of those can be absent on a perfectly
/// healthy deployment, so the seam reports it rather than throwing — and a caller that ignores
/// <see cref="IsAvailable"/> gets <see langword="null"/> from <see cref="EmbedAsync"/> rather than a plausible
/// wrong vector.
/// </para>
/// <para>
/// <b>Why null rather than an empty vector.</b> A zero-length or zero-filled vector is a <i>valid</i> input to a
/// similarity search: it returns results, all equally meaningless. Retrieval that silently degrades to noise is
/// worse than retrieval that says it cannot run — the same reasoning that makes an unreachable venue
/// declared-unknown rather than an empty book (gh#381).
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

    /// <summary>Embeds <paramref name="text"/>, or returns <see langword="null"/> when it cannot.</summary>
    /// <param name="text">The content to embed.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The vector, or <see langword="null"/> when embedding is unavailable or failed.</returns>
    Task<IReadOnlyList<float>?> EmbedAsync(string text, CancellationToken cancellationToken);
}
