namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A single-row marker of when the global relevance config (ticker maps + topics) last changed (gh#359). The
/// resolution pass re-resolves any news whose <c>RelevanceResolvedAt</c> predates this, so a config edit
/// re-resolves affected news <b>predictably</b> without having to touch every news row at edit time. Global system
/// plumbing, not <c>IUserOwned</c>.
/// </summary>
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

    /// <summary>When the ticker maps or topics last changed.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
