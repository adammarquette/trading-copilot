namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A single-row marker of the global relevance config's generation (ticker maps + topics; gh#359, hardened
/// gh#418). Every config mutation increments <see cref="Version"/>; the resolution pass re-resolves any news
/// whose <c>RelevanceVersion</c> is below it, so a config edit re-resolves affected news <b>predictably</b>
/// without touching every news row at edit time. Global system plumbing, not <c>IUserOwned</c>.
/// </summary>
/// <remarks>
/// A <b>monotonic version</b>, not a wall clock (gh#418). The earlier design compared timestamps
/// (<c>RelevanceResolvedAt &lt; UpdatedAt</c>), which had two narrow gaps its own review surfaced: a config edit
/// whose commit landed mid-pass could leave rows stamped fresh against stale config, and per-instance clock skew
/// in a scaled deployment could cause re-resolve storms. A counter that only advances on commit closes both —
/// there are no clocks to disagree.
/// </remarks>
public sealed class RelevanceConfigState
{
    /// <summary>
    /// The fixed id of the one config-state row per deployment. Upserting by this constant (rather than a random
    /// id) guarantees the table can never fork into two rows — which would let config edits and the resolution
    /// pass read different rows and silently strand re-resolution.
    /// </summary>
    public static readonly Guid SingletonId = new("5e1e0359-0000-4000-8000-000000000359");

    /// <summary>The row id (always <see cref="SingletonId"/>).</summary>
    public Guid Id { get; set; }

    /// <summary>When the ticker maps or topics last changed. <b>Observability only</b> (gh#418) — no longer the staleness signal.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The monotonic config generation, incremented on every edit — the authoritative staleness signal (gh#418).</summary>
    public long Version { get; set; }
}
