using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One operator's feedback on a news / soft-signal item (gh#27, gh#762, ADR-0014) — one store spanning <b>two
/// independent axes</b> (<see cref="SoftSignalAxis"/>): <b>importance</b> (<see cref="SoftSignalKind.Star"/> and its
/// <see cref="SoftSignalKind.Mute"/> inverse → salience) and <b>direction</b> (👍/👎
/// <see cref="SoftSignalKind.ThumbsUp"/> / <see cref="SoftSignalKind.ThumbsDown"/> → R-9 learning). One feedback model
/// for all kinds; an operator may hold at most one row per axis on an item, independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operator-owned (<see cref="IUserOwned"/>).</b> A star is one operator's priority, not another's (R-20 /
/// ADR-0017) — so it carries the owning <see cref="UserId"/> and the default-deny filter applies. This is the
/// per-user counterpart to the <b>global</b> <see cref="NewsRecord"/> it rates: the news item is shared, the
/// operator's opinion of it is not.
/// </para>
/// <para>
/// It references the rated item by its <see cref="NewsDedupKey"/> (the news primary key) rather than a navigation,
/// keeping the per-user table decoupled from the global news store. At most one row per operator, item, <b>and
/// axis</b> — <b>two filtered unique indexes</b> over <c>(UserId, NewsDedupKey)</c>, one per axis (gh#762) — so
/// re-rating within an axis replaces rather than stacks and leaves the other axis untouched; clearing deletes only
/// that axis's row.
/// </para>
/// <para>
/// <b>Importance is a soft salience weight; direction is salience-inert; neither is a risk control</b> (ADR-0007 /
/// ADR-0014). The importance row feeds the news surfacing / retrieval salience (the <see cref="SalienceScorer"/>); the
/// direction row feeds only R-9 learning + the read surface and never reweights surfacing. Both are structurally
/// unreachable from the risk gate or order sizing.
/// </para>
/// </remarks>
public sealed class SoftSignalFeedback : IUserOwned
{
    /// <summary>The feedback row's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The rated item's dedup key — the <see cref="NewsRecord.DedupKey"/> primary key (a canonicalized URL).</summary>
    public required string NewsDedupKey { get; set; }

    /// <summary>
    /// The kind of feedback — importance (<see cref="SoftSignalKind.Star"/> / <see cref="SoftSignalKind.Mute"/>) or
    /// direction (<see cref="SoftSignalKind.ThumbsUp"/> / <see cref="SoftSignalKind.ThumbsDown"/>). Its axis
    /// (<see cref="SoftSignalKindExtensions.Axis"/>) selects which filtered unique index it belongs to.
    /// </summary>
    public SoftSignalKind Kind { get; set; }

    /// <summary>
    /// When the operator gave this feedback (UTC) — the age input to the salience recency decay for an importance row
    /// (gh#27); for a direction row it is a plain provenance timestamp (direction is age-agnostic at rest, gh#762).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
