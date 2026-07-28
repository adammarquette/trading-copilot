using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The default <see cref="IEmbeddingProvider"/> (gh#109): reports itself unavailable and embeds nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The substrate must be usable with no API key and no spend</b>, so this — not the Cohere adapter (gh#403) —
/// is what is registered until a real provider is configured. Every downstream test therefore runs without
/// external credentials.
/// </para>
/// <para>
/// <b>It says so once, at error, on first use.</b> A deployment with semantic retrieval quietly switched off is
/// the failure gh#109's degrade decision exists to prevent; a provider that returned an empty vector instead
/// would make retrieval return results that are uniformly meaningless — worse than refusing, because it looks
/// like an answer.
/// </para>
/// </remarks>
public sealed class UnavailableEmbeddingProvider : IEmbeddingProvider
{
    private readonly ILogger<UnavailableEmbeddingProvider> _logger;
    private int _announced;

    /// <summary>Creates the provider.</summary>
    /// <param name="logger">The logger.</param>
    public UnavailableEmbeddingProvider(ILogger<UnavailableEmbeddingProvider> logger) => _logger = logger;

    /// <inheritdoc />
    public string Model => "none";

    /// <inheritdoc />
    public int Dimensions => TradingCopilotDbContext.EmbeddingDimensions;

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<float>?> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        // Once, not per call: a retrieval loop would otherwise turn a configuration state into a log flood, and
        // a flooding error is one that gets filtered out.
        if (Interlocked.Exchange(ref _announced, 1) == 0)
        {
            _logger.LogError(
                "No embedding provider is configured, so semantic retrieval is OFF (gh#109). Configure the Cohere "
                + "provider (gh#403) to enable it. Nothing on the trading path depends on this.");
        }

        return Task.FromResult<IReadOnlyList<float>?>(null);
    }
}
