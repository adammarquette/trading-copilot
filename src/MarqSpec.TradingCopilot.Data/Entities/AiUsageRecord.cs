using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One AI invocation's spend (gh#431, ADR-0008 / ADR-0002) — an append-only, per-owner ledger row: the feature it
/// served, the model / tier, tokens in and out, an estimated dollar cost, latency, the trace id, and when. The
/// durable read model behind the in-app spend meter and the input to the platform spend governor (gh#448).
/// </summary>
/// <remarks>
/// Operator-owned (R-20) and append-only — a usage row is never updated. It is a spend <b>floor</b>, not a complete
/// accounting: the fail-open ledger records nothing if the host dies between the call and the write. The platform
/// spend governor (gh#448) enforces on this floor because it is the only app-queryable spend signal — the aggregate
/// <c>MarqSpec.TradingCopilot.Ai</c> meter (ADR-0002) is export-only (Prometheus), not readable in-process — so the
/// effective cap is the budget plus at most one in-flight call's un-recorded spend; the operator reconciles the
/// floor against Grafana and sets the budget with headroom (ADR-0008, amended by gh#448). A
/// <see cref="AiUsageOutcome.Failed"/> row carries zero tokens — a real datapoint (the call cost latency and
/// produced nothing), not an absence.
/// </remarks>
public class AiUsageRecord : IUserOwned
{
    /// <summary>The record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — the single-provenance owner from the recording consumer's scope.</summary>
    public Guid UserId { get; set; }

    /// <summary>What the call was for.</summary>
    public AiUsageFeature Feature { get; set; }

    /// <summary>The concrete model id billed (e.g. <c>claude-haiku-4-5</c>).</summary>
    public required string Model { get; set; }

    /// <summary>The model tier, or <see langword="null"/> for a provider with no tier (e.g. embeddings).</summary>
    public LlmModelTier? Tier { get; set; }

    /// <summary>How the call ended, for spend accounting.</summary>
    public AiUsageOutcome Outcome { get; set; }

    /// <summary>Prompt tokens billed (zero for a failed call).</summary>
    public int InputTokens { get; set; }

    /// <summary>Completion tokens billed (zero for a failed call).</summary>
    public int OutputTokens { get; set; }

    /// <summary>The estimated dollar cost of those tokens at the pinned rate.</summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>Wall-clock time the call took, in milliseconds.</summary>
    public long LatencyMs { get; set; }

    /// <summary>The W3C trace id of the invocation (ADR-0002), or <see langword="null"/> if none was active.</summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// The trigger firing this call served (gh#767), or <see langword="null"/> for a call with no firing (a chat
    /// turn, a news embedding). A <b>soft link</b> — it matches <see cref="Suggestion.TriggerFiringId"/>, so a
    /// suggestion's total AI cost is the sum of the rows sharing its firing; not an FK, because the fail-open ledger
    /// outlives the trigger it references (the same reason <see cref="Suggestion"/> soft-links the firing).
    /// </summary>
    public Guid? TriggerFiringId { get; set; }

    /// <summary>When the call happened (caller-supplied; the ledger never reads a clock).</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
