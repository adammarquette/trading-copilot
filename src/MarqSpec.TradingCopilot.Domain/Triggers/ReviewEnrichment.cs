namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The extra <b>numeric</b> market context handed to the <b>deep</b> reviewer only (gh#476, R-4 / ADR-0008): a short
/// window of recent OHLCV bars and recent values of the <i>fired indicator's own series</i>, as of the fire.
/// </summary>
/// <remarks>
/// <para>
/// A deep call happens only when the cheap triage pass judged the setup genuinely too hard and escalated (gh#449).
/// That call is asked for numeric entry/stop/target, yet the base <see cref="TriggerReviewContext"/> carries
/// <b>no price</b> — only the fired reading and its threshold. This payload is the price and recent-trajectory
/// context the deep tier needs to reason, and it is deliberately <b>numeric-only</b>: every field is a
/// <see langword="decimal"/>, a <see langword="long"/>, or a timestamp, so the prompt-injection surface stays
/// near zero (the same invariant <see cref="TriggerReviewContext"/> holds). Free-text sources (news headlines) are
/// the injection surface and are <b>deferred</b> to a later increment.
/// </para>
/// <para>
/// <b>Deep-only and additive.</b> The triage prompt is unchanged — enrichment rides a <b>trailing optional</b> field
/// on the context, so the cheap tier's render is byte-for-byte what it was and only the escalated deep call sees the
/// extra block. It is <b>enrichment, not a limit</b>: nothing here gates or sizes anything (enforcement lives below
/// the model). The source assembles it in the deterministic scan and fails open — a read fault yields no enrichment
/// and an un-enriched deep call, never a lost fire.
/// </para>
/// </remarks>
/// <param name="RecentBars">Recent OHLCV bars for the instrument/resolution, oldest-first, as of the fire; possibly empty.</param>
/// <param name="RecentIndicatorValues">Recent values of the fired indicator's own series, oldest-first, as of the fire; possibly empty.</param>
public sealed record ReviewEnrichment(
    IReadOnlyList<BarSnapshot> RecentBars,
    IReadOnlyList<IndicatorValueSnapshot> RecentIndicatorValues);

/// <summary>One recent OHLCV bar in a <see cref="ReviewEnrichment"/> — a numeric read-model row, prices as decimals.</summary>
/// <param name="BucketStart">When the bar opened (UTC).</param>
/// <param name="Open">The opening price.</param>
/// <param name="High">The highest traded price in the bar.</param>
/// <param name="Low">The lowest traded price in the bar.</param>
/// <param name="Close">The closing price.</param>
/// <param name="Volume">The traded volume in the bar.</param>
public sealed record BarSnapshot(
    DateTimeOffset BucketStart,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

/// <summary>One recent value of the fired indicator's series in a <see cref="ReviewEnrichment"/> — numeric only.</summary>
/// <param name="BucketStart">The bar bucket the value belongs to (UTC).</param>
/// <param name="Value">The computed indicator value.</param>
public sealed record IndicatorValueSnapshot(DateTimeOffset BucketStart, decimal Value);

/// <summary>
/// Assembles the deep tier's <see cref="ReviewEnrichment"/> for a fired trigger (gh#476) — a read-only seam consulted
/// in the deterministic scan, <b>never</b> by the reviewer. Keeping it in the scan (not injected into the reviewer)
/// is what keeps the reviewer's dependency set pure of execution and data-access types (gate-below-model, gh#402).
/// </summary>
/// <remarks>
/// It reads at or <b>before</b> <see cref="TriggerReviewContext.FiredAt"/> — never after — the same bar-close-aligned
/// cutoff the fired reading itself came through, so the enrichment is consistent with the decision (strictly
/// no-look-ahead on a live fire; on a replay the boundary bucket reflects its completed bar, exactly as the fired
/// value does). Returns <see langword="null"/> when there is nothing to add (both series empty), so the caller can
/// pass an un-enriched context unchanged.
/// </remarks>
public interface IReviewEnrichmentSource
{
    /// <summary>Builds the deep-tier enrichment for a fired trigger, as of <see cref="TriggerReviewContext.FiredAt"/>.</summary>
    /// <param name="context">The fired setup's market facts (the instrument, indicator series, and fire time to read as of).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The numeric enrichment, or <see langword="null"/> when there is nothing to add.</returns>
    Task<ReviewEnrichment?> BuildAsync(TriggerReviewContext context, CancellationToken cancellationToken);
}
