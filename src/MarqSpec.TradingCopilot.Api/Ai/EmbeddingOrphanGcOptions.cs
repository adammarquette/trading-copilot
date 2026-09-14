namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// How often the embedding orphan-GC sweep runs (gh#902). Bound from the <c>EmbeddingGc</c> config section.
/// </summary>
/// <remarks>
/// <b>Conservative by default.</b> Orphaning is rare (a renamed <c>NewsTopic</c> or a pruned <c>NewsRecord</c>), the
/// leak it reclaims is bounded (storage + a marginal recall slot, never a wrong answer — gh#902), and the delete is
/// an <i>irreversible</i>, paid re-embed if the owner returns. So this runs off the hot embed path on a long cadence,
/// not every minute like <see cref="NewsEmbeddingOptions"/>. The 6-hour default reclaims within a quarter-day of an
/// orphaning while keeping the sweep far from the ingest cadence.
/// </remarks>
public sealed class EmbeddingOrphanGcOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "EmbeddingGc";

    /// <summary>How often the orphan sweep runs, in seconds. Default 6 hours.</summary>
    public int SweepIntervalSeconds { get; init; } = 21600;

    /// <summary>The sweep cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(SweepIntervalSeconds);
}
