namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>What an AI invocation was <b>for</b> — the feature dimension on every AIUsage row (gh#431, ADR-0008).</summary>
/// <remarks>
/// The full data-dictionary domain. <see cref="Triage"/> (the agent-review reviewer) and <see cref="Embed"/> (the
/// news-embedding pass) have producers today; the rest are reserved for their features as they land (a
/// suggestion-enrich pass, follow-ups, backtests).
/// </remarks>
public enum AiUsageFeature
{
    /// <summary>Unset — refused by a DB CHECK so a row can never be silently feature-less.</summary>
    Unknown = 0,

    /// <summary>A suggestion-generation / enrichment call (reserved — no producer yet).</summary>
    Suggestion = 1,

    /// <summary>A review / follow-up conversation call (reserved — no producer yet).</summary>
    FollowUp = 2,

    /// <summary>A backtest-time call (reserved — no producer yet).</summary>
    Backtest = 3,

    /// <summary>The agent-review "is this worth surfacing?" triage call (gh#402 / gh#423).</summary>
    Triage = 4,

    /// <summary>
    /// An embedding call (gh#403). Owner attribution is settled (gh#436): a <b>global</b> embed (deployment news /
    /// snapshots) is stamped to the deployment sentinel <c>SystemOwner</c>, an owner-scoped embed stamps the operator.
    /// The live producer is the news-embedding pass (gh#377), which ledgers every attempted call — success or
    /// failure alike.
    /// </summary>
    Embed = 5,

    /// <summary>
    /// A co-pilot chat-turn call (gh#906, R-6). The grounded chat turn ledgers one row per turn against the operator,
    /// success or failure alike, and is governed by the same daily AI-spend budget as agent review.
    /// </summary>
    Chat = 6,
}

/// <summary>
/// How an AI call ended, for spend accounting — <b>orthogonal to whether the review was usable</b> (gh#431).
/// </summary>
/// <remarks>
/// A completion that the reviewer suppresses (not-worth-surfacing, malformed JSON) still <see cref="Succeeded"/>
/// here: it <i>completed and was billed</i>. The discriminator that matters for spend is whether tokens were
/// charged, not whether the answer was useful. <see cref="Failed"/> is the failure regime the governor most needs
/// to see — a provider fault (429 / 5xx / timeout) that cost latency and produced no usage.
/// </remarks>
public enum AiUsageOutcome
{
    /// <summary>Unset — refused by a DB CHECK.</summary>
    Unknown = 0,

    /// <summary>The provider returned a completion (billed), whatever the reviewer then did with it.</summary>
    Succeeded = 1,

    /// <summary>The model refused (a <c>refusal</c> stop).</summary>
    Refused = 2,

    /// <summary>The response hit the output-token ceiling (a <c>max_tokens</c> stop) — billed but truncated.</summary>
    Truncated = 3,

    /// <summary>A provider fault (transport error, timeout, 4xx/5xx) — no usage; the governor's failure signal.</summary>
    Failed = 4,

    /// <summary>Rate-limited — a 429 the embed path degrades to sparse for (gh#403); ledgered by the news-embedding pass (gh#377).</summary>
    RateLimited = 5,
}

/// <summary>
/// The provider-neutral cost of <b>one</b> AI call, computed where the call is made (gh#431) — tokens, an estimated
/// dollar cost, latency, and the feature/model/tier/outcome dimensions.
/// </summary>
/// <remarks>
/// This is what a consumer surfaces <i>up</i> from an AI call; the raw <c>LlmUsage</c> stays encapsulated behind it,
/// so no provider detail leaks past the seam. The tenancy owner is deliberately <b>not</b> here — it is stamped by
/// the owner-scoped consumer (the scan), which is the single authority on whose call this was.
/// </remarks>
/// <param name="Feature">What the call was for.</param>
/// <param name="Model">The concrete model id billed (e.g. <c>claude-haiku-4-5</c>).</param>
/// <param name="Tier">The model tier, or <see langword="null"/> for a provider with no tier (e.g. embeddings).</param>
/// <param name="Outcome">How the call ended, for spend accounting.</param>
/// <param name="InputTokens">Prompt tokens billed (zero for a failed call).</param>
/// <param name="OutputTokens">Completion tokens billed (zero for a failed call).</param>
/// <param name="EstimatedCostUsd">The estimated dollar cost of those tokens at a pinned rate.</param>
/// <param name="Latency">Wall-clock time the call took, success or failure.</param>
public sealed record AiCallCost(
    AiUsageFeature Feature,
    string Model,
    LlmModelTier? Tier,
    AiUsageOutcome Outcome,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostUsd,
    TimeSpan Latency);

/// <summary>
/// One ledger entry: the <see cref="AiCallCost"/> plus the tenancy + tracing the owner-scoped consumer stamps on it
/// (gh#431).
/// </summary>
/// <param name="UserId">The owner whose call this was (R-20) — the single-provenance owner from the consumer's scope.</param>
/// <param name="Cost">The per-call cost.</param>
/// <param name="TraceId">The W3C trace id of the invocation (ADR-0002), or <see langword="null"/> if none was active.</param>
/// <param name="OccurredAt">When the call happened — caller-supplied; the ledger never reads a clock.</param>
/// <param name="TriggerFiringId">
/// The trigger firing this call served (gh#767), or <see langword="null"/> for a call with no firing (a chat turn, a
/// news embedding). It is the per-suggestion cost key: a suggestion's total AI cost is the sum of its firing's rows,
/// joined on the firing the suggestion also carries. Defaulted so the firing-less consumers need not pass it.
/// </param>
public sealed record AiUsageEntry(
    Guid UserId, AiCallCost Cost, string? TraceId, DateTimeOffset OccurredAt, Guid? TriggerFiringId = null);

/// <summary>
/// Persists one AIUsage row per AI invocation (gh#431, ADR-0008 / ADR-0002) — the durable per-owner ledger behind
/// the in-app spend meter and the (future) platform spend governor's input.
/// </summary>
/// <remarks>
/// <para>
/// A <b>required</b> dependency of the recording path, never optional — an optional metrics/ledger dependency
/// silently defaults to no spend visibility in production, which is the failure this exists to prevent.
/// </para>
/// <para>
/// <b>Fail-open by contract.</b> A ledger write must <b>never</b> fail or roll back the AI call it records — a fired
/// setup's suggestion must not be lost because bookkeeping hiccuped. Implementations catch their own faults (logged,
/// never silently swallowed) and return; only a genuine caller cancellation propagates. The ledger is therefore a
/// durable <b>floor</b> on spend, not a complete accounting — a crash between the call and the write loses a row.
/// </para>
/// </remarks>
public interface IAiUsageLedger
{
    /// <summary>Records one AI call. Never throws except on the caller's own cancellation.</summary>
    /// <param name="entry">The call's cost + tenancy.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task RecordAsync(AiUsageEntry entry, CancellationToken cancellationToken);
}

/// <summary>The no-op ledger for a context with no persistence — tests and any deliberately-ledgerless path.</summary>
public sealed class NullAiUsageLedger : IAiUsageLedger
{
    /// <summary>The shared instance.</summary>
    public static NullAiUsageLedger Instance { get; } = new();

    /// <inheritdoc />
    public Task RecordAsync(AiUsageEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
}
