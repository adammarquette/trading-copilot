using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A durable rulebook rule (gh#866, R-7, data dictionary §8) — the practice the operator expressed, persisted as
/// intent plus an optional structured form. Operator-owned (R-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Inert until confirmed.</b> <see cref="Enabled"/> and <see cref="Confirmed"/> both default false, so a newly
/// authored row can never read as live. <see cref="IsArmed"/> is the single predicate: both switches must be on.
/// Confirmation is distinct from enabled, matching the trigger gate (gh#470) — authorship arms nothing.
/// </para>
/// <para>
/// <see cref="StructuredForm"/> is <b>storage</b> for a compiled condition, not a compiler. The NL→condition
/// compiler stays on gh#489; this column is the place its output will land.
/// </para>
/// <para>
/// <see cref="SourceConversationId"/> is a <b>soft</b> reference (no FK, no navigation), matching
/// <see cref="TriggerRecord.SourceRuleId"/> / <see cref="TriggerRecord.SourceConversationId"/>, so the rule
/// outlives the conversation it was authored in.
/// </para>
/// <para>
/// The §8 <b>VEC</b> half is an <see cref="EmbeddingRecord"/> under <see cref="EmbeddingOwnerKind.Rule"/>, keyed
/// by <see cref="EmbeddingOwnerId"/>. This entity does not write embeddings — it only names the owner the
/// existing store already reserved (gh#109).
/// </para>
/// </remarks>
public class Rule : IUserOwned
{
    /// <summary>How long the plain-language intent may be (characters).</summary>
    public const int IntentTextMaxLength = 4096;

    /// <summary>The rule's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The plain-language practice as authored.</summary>
    public required string IntentText { get; set; }

    /// <summary>
    /// The compiled deterministic condition, when one exists — storage for the compiler's output, not the
    /// compiler. <see langword="null"/> until compiled (gh#489).
    /// </summary>
    public string? StructuredForm { get; set; }

    /// <summary>Whether the rule is on. Distinct from <see cref="Confirmed"/>; defaults off (inert).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the operator has accepted this rule. Defaults off, so a new row is inert regardless of
    /// <see cref="Enabled"/>.
    /// </summary>
    public bool Confirmed { get; set; }

    /// <summary>
    /// The <see cref="Conversation"/> this rule was authored in, when it came from chat — a <b>soft</b>
    /// reference (no FK), so the rule outlives the conversation.
    /// </summary>
    public Guid? SourceConversationId { get; set; }

    /// <summary>
    /// The Instrument / RelevanceConfig metadata the rule resolved against <b>at confirmation</b> (data
    /// dictionary §8). <see langword="null"/> until confirmed. If that metadata later changes, set
    /// <see cref="NeedsRevalidation"/> rather than silently retargeting.
    /// </summary>
    public string? InstrumentDependencySnapshot { get; set; }

    /// <summary>
    /// Whether the confirmation-time snapshot is stale (symbol reclassified, topic remapped) and the rule
    /// needs review before it can be trusted against the current instrument map.
    /// </summary>
    public bool NeedsRevalidation { get; set; }

    /// <summary>When the rule was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The <see cref="EmbeddingRecord.OwnerId"/> for this rule's vector row — the §8 VEC address, not a write.
    /// </summary>
    public string EmbeddingOwnerId => Id.ToString("D");

    /// <summary>
    /// Whether this rule may affect live behaviour. False until the operator has both confirmed and enabled it.
    /// </summary>
    /// <returns><see langword="true"/> only when <see cref="Enabled"/> and <see cref="Confirmed"/> are both set.</returns>
    public bool IsArmed() => Enabled && Confirmed;
}
