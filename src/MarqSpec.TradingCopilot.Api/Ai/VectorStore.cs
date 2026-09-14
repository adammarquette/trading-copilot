namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Whether this deployment actually has somewhere to put a vector (gh#474) — the <c>pgvector</c> extension, probed
/// once at startup and held for the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The <c>AddEmbeddingStore</c> migration (gh#109) is deliberately conditional: on a
/// Postgres without <c>pgvector</c> it <i>skips the table and starts anyway</i>, because refusing to start would
/// let an unavailable retrieval feature take down the safety-critical auto-flatten (R-13). Its remarks promised
/// that the absence would be <b>declared</b> — that <c>IEmbeddingProvider.IsAvailable</c> would report false. That
/// half was never built, so a deployment with a key but no extension embedded through Cohere on every poll
/// (<b>real spend</b>) and faulted at the upsert every time: logged, harmless to trading, and never self-healing.
/// </para>
/// <para>
/// <b>Probed once, not per call.</b> The extension cannot appear or disappear without a migration, so this is a
/// process-lifetime fact. Checking it per call would put a database round trip behind a synchronous property; the
/// startup probe follows the same shape as the kill-switch rehydration in <c>StartupTasks</c>.
/// </para>
/// <para>
/// <b>Unknown is not available.</b> The default is <see langword="false"/>, so a caller that races the probe
/// declines rather than spends. That is the fail-closed direction: the cost of a wrongly-declined embed is a
/// sparse retrieval result, and the cost of a wrongly-permitted one is money for a vector with nowhere to go.
/// </para>
/// </remarks>
public sealed class VectorStore
{
    private volatile bool _present;

    /// <summary>
    /// Whether a vector can actually be stored. <see langword="false"/> until the startup probe says otherwise.
    /// </summary>
    public bool IsPresent => _present;

    /// <summary>Records what the startup probe found.</summary>
    /// <param name="present">Whether the <c>pgvector</c> extension is installed on this database.</param>
    public void Record(bool present) => _present = present;
}
