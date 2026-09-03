using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The one mapping from the domain's consumer-facing <see cref="RetrievalKind"/> to the store's persisted
/// <see cref="EmbeddingOwnerKind"/> (gh#1065) — the Api-tier seam that lets the Domain retrieval contract stay free of
/// any <c>Data</c> dependency (the same reason gh#854 kept <c>GetTopicVectorsAsync</c> a named method rather than an
/// owner-kind parameter).
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, deliberately.</b> The store's owner kinds are part of a persisted composite primary key, so their
/// numeric values can never move; the retrieval kinds are a selector a consumer passes. A second copy of this mapping
/// is a silent data corruption waiting to happen — one that would write vectors under one kind and read them under
/// another — so every reader and writer of an owner-scoped embedding routes through here.
/// </para>
/// <para>
/// <b>An unmappable kind throws.</b> <see cref="RetrievalKind.Unknown"/> is the refusable zero and is never persisted;
/// asking for it (or for a value cast in from outside the enum) is a programming error, not a degraded deployment, so
/// it fails loudly rather than quietly reading the wrong kind's rows.
/// </para>
/// </remarks>
public static class EmbeddingOwnerKinds
{
    /// <summary>Maps a retrievable kind onto the owner kind its vectors are stored under.</summary>
    /// <param name="kind">The consumer-facing retrieval kind.</param>
    /// <returns>The persisted owner kind for that retrieval kind.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind has no persisted owner kind.</exception>
    public static EmbeddingOwnerKind For(RetrievalKind kind) => kind switch
    {
        RetrievalKind.News => EmbeddingOwnerKind.SoftSignal,
        RetrievalKind.Suggestion => EmbeddingOwnerKind.Suggestion,
        RetrievalKind.JournalEntry => EmbeddingOwnerKind.JournalEntry,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "That retrieval kind has no stored embedding owner kind."),
    };
}
