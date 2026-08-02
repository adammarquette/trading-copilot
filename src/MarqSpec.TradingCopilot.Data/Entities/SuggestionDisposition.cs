using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The operator's disposition of a <see cref="Suggestion"/> (data dictionary §6, R-4 / R-8) — the input the R-9
/// learning loop reads. <b>Append-only journal evidence</b>: one disposition per suggestion, never mutated, and it
/// never changes the suggestion's trade parameters.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary is settled (gh#539): <see cref="Kind"/> records an <b>operator act only</b>
/// (<see cref="SuggestionDispositionKind.Taken"/> / <see cref="SuggestionDispositionKind.Modified"/> /
/// <see cref="SuggestionDispositionKind.Passed"/>) and is separate from the clock-driven
/// <see cref="Suggestion.State"/>. This card (gh#547) writes only <see cref="SuggestionDispositionKind.Passed"/>.
/// </para>
/// <para>
/// <b>One per suggestion</b> is enforced by a unique index on <see cref="SuggestionId"/> — a second disposition
/// <i>conflicts</i> rather than overwriting, because the journal records what the operator decided, not their latest
/// edit. <see cref="IUserOwned"/>, so the R-20 default-deny filter applies automatically.
/// </para>
/// </remarks>
public class SuggestionDisposition : IUserOwned
{
    /// <summary>The longest an operator's <see cref="Note"/> may be. Shared by the DB config and the endpoint guard.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>The disposition's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — always the suggestion's owner. Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The suggestion this disposes. Unique: one disposition per suggestion.</summary>
    public Guid SuggestionId { get; set; }

    /// <summary>
    /// The operator act. <see cref="SuggestionDispositionKind.Unknown"/> is refused by a DB check; this card only ever
    /// writes <see cref="SuggestionDispositionKind.Passed"/>.
    /// </summary>
    public required SuggestionDispositionKind Kind { get; set; }

    /// <summary>
    /// The pass reasons (a <c>[Flags]</c> multi-select). <see cref="SuggestionPassReason.None"/> is valid — a pass is
    /// a neutral decline and its reason is optional (R-4), so this column carries no refusable-zero check.
    /// </summary>
    public required SuggestionPassReason Reasons { get; set; }

    /// <summary>An optional free-text note, capped at <see cref="NoteMaxLength"/>. <see langword="null"/> when omitted.</summary>
    public string? Note { get; set; }

    /// <summary>When the operator recorded the disposition.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
