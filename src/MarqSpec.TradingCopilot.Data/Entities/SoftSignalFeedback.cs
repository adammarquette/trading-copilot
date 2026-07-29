using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One operator's feedback on a news / soft-signal item (gh#27, ADR-0014) — the store behind importance
/// <b>starring</b> and its <b>mute</b> inverse, and the entity that will also carry the deferred 👍/👎 sentiment
/// axis (one feedback model for all three; R-2).
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
/// keeping the per-user table decoupled from the global news store. One feedback per operator per item — a unique
/// <c>(UserId, NewsDedupKey)</c> index — so re-rating replaces rather than stacks; un-starring deletes the row.
/// </para>
/// <para>
/// <b>A soft salience weight, never a risk control</b> (ADR-0007 / ADR-0014): this row feeds only the news
/// surfacing / retrieval salience (the <see cref="SalienceScorer"/>) and is structurally unreachable from the risk
/// gate or order sizing.
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

    /// <summary>The kind of feedback — <see cref="SoftSignalKind.Star"/> or <see cref="SoftSignalKind.Mute"/> today.</summary>
    public SoftSignalKind Kind { get; set; }

    /// <summary>When the operator gave this feedback (UTC) — the age input to the salience recency decay (gh#27).</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
