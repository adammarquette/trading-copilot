namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// Records the spend of every agent-review <b>LLM</b> call — call count, tokens in/out, estimated cost and latency —
/// on the same AI meter as embeddings (gh#477, ADR-0002 / ADR-0008), so Prometheus/Grafana show <b>true total AI
/// spend</b> (LLM + embed), not just the embedding half (gh#403).
/// </summary>
/// <remarks>
/// <para>
/// The LLM counterpart of <see cref="IEmbeddingMetrics"/>, fed from the <see cref="AiCallCost"/> the reviewer already
/// produces per call (gh#431/gh#449) — so an escalated fire meters <b>two</b> calls (triage + deep), one per billed
/// call, exactly as it writes two ledger rows. Dimensioned by <c>model</c>, <c>tier</c> and <c>outcome</c> only — all
/// low-cardinality — so the series count cannot explode; the reviewed text is <b>never</b> a tag.
/// </para>
/// <para>
/// A <b>required</b> dependency of the recording path, never optional — an optional metrics dependency silently
/// defaults to no spend visibility in production, which is the failure this exists to prevent (the gh#403 posture).
/// <b>Observability, not enforcement:</b> a <c>System.Diagnostics.Metrics</c> meter is export-only / not app-readable,
/// so this does <b>not</b> feed the ADR-0008 spend governor (which enforces on the persisted <c>AIUsage</c> ledger
/// floor); it closes the Grafana-visibility gap only.
/// </para>
/// </remarks>
public interface ILlmMetrics
{
    /// <summary>Records one billed LLM call, whatever its outcome (a failed / refused / truncated call is metered too).</summary>
    /// <param name="cost">The call's priced facts (model, tier, outcome, tokens in/out, estimated USD cost, latency).</param>
    void RecordLlmCall(AiCallCost cost);
}
