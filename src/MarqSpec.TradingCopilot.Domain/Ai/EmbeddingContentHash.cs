using System.Security.Cryptography;
using System.Text;

namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// Derives the stable content hash an <c>EmbeddingRecord</c> is keyed against (gh#377) — the idempotence guard that
/// lets a re-embed pass recognise an <b>unchanged</b> owner and skip the paid call, the embedding analogue of how
/// <see cref="MarqSpec.TradingCopilot.Domain.Venue.NewsDedupKey"/> derives news's own identity key.
/// </summary>
/// <remarks>
/// A pure function of the exact text handed to the embedding provider, so it is unit-testable with no database and
/// no provider: the same content always hashes to the same value, and a changed title or summary always hashes to a
/// different one. SHA-256 hex, lower-case — 64 characters, fitting the <c>ContentHash</c> column's
/// <c>character varying(64)</c> exactly.
/// </remarks>
public static class EmbeddingContentHash
{
    /// <summary>Computes the stable hash of <paramref name="content"/>.</summary>
    /// <param name="content">The exact text that was, or would be, embedded.</param>
    /// <returns>A 64-character lower-case hex SHA-256 digest.</returns>
    public static string For(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
