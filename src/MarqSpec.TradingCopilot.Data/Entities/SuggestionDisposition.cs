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
/// <see cref="Suggestion.State"/>. gh#547 writes <see cref="SuggestionDispositionKind.Passed"/>; gh#549 adds the take
/// path's <see cref="SuggestionDispositionKind.Taken"/> / <see cref="SuggestionDispositionKind.Modified"/>, the latter
/// recording the field-level <see cref="Deviations"/> and the take-time snapshot the R-9 loop reads.
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

    /// <summary>
    /// Which fields a <see cref="SuggestionDispositionKind.Modified"/> take deviated from the suggestion on (gh#549).
    /// <see cref="SuggestionDeviation.None"/> for an unmodified <see cref="SuggestionDispositionKind.Taken"/> take and
    /// for a <see cref="SuggestionDispositionKind.Passed"/> disposition (never taken).
    /// </summary>
    public SuggestionDeviation Deviations { get; set; }

    /// <summary>The entry price actually submitted at take time (gh#549) — the snapshot R-9 reads against the suggested value; <see langword="null"/> for a pass.</summary>
    public decimal? TakenEntryPrice { get; set; }

    /// <summary>The working (protective) stop actually submitted at take time; <see langword="null"/> for a pass.</summary>
    public decimal? TakenStopPrice { get; set; }

    /// <summary>The take-profit target actually submitted at take time; <see langword="null"/> when the take carried none, or for a pass.</summary>
    public decimal? TakenTargetPrice { get; set; }

    /// <summary>The size actually submitted at take time; <see langword="null"/> for a pass.</summary>
    public int? TakenSize { get; set; }

    /// <summary>
    /// Builds the disposition for a suggestion just <b>taken</b> — the take path's write (gh#549). Compares the
    /// submitted parameters against the suggestion by <b>exact decimal value</b> (no tolerance — a modified take must
    /// never be recorded as taken, R-9 integrity) to choose <see cref="SuggestionDispositionKind.Taken"/> vs
    /// <see cref="SuggestionDispositionKind.Modified"/> and record which fields deviated. Pure: it reads no clock (the
    /// caller passes <paramref name="now"/>) and no I/O, so it is unit-testable off the database.
    /// </summary>
    /// <param name="suggestion">The suggestion being taken — the owner (R-20) and the suggested parameters.</param>
    /// <param name="submittedEntry">The entry actually sent (the order's <c>EntryPrice</c>).</param>
    /// <param name="submittedStop">The working stop actually sent (the order's <c>WorkingStopPrice</c> — the operator's protective stop, not the safety stop).</param>
    /// <param name="submittedTarget">The take-profit actually sent (the order's <c>TakeProfitPrice</c>), or <see langword="null"/> if the take carried none.</param>
    /// <param name="submittedSize">The size actually sent (the order's <c>Size</c>).</param>
    /// <param name="now">The record time, supplied by the caller — the domain never reads a clock.</param>
    public static SuggestionDisposition ForTake(
        Suggestion suggestion,
        decimal submittedEntry,
        decimal submittedStop,
        decimal? submittedTarget,
        int submittedSize,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        SuggestionDeviation deviations = SuggestionDeviation.None;
        if (submittedEntry != suggestion.EntryPrice) { deviations |= SuggestionDeviation.Entry; }
        if (submittedStop != suggestion.StopPrice) { deviations |= SuggestionDeviation.Stop; }
        if (submittedTarget != suggestion.TargetPrice) { deviations |= SuggestionDeviation.Target; }
        if (submittedSize != suggestion.Size) { deviations |= SuggestionDeviation.Size; }

        return new SuggestionDisposition
        {
            Id = Guid.NewGuid(),
            UserId = suggestion.UserId,
            SuggestionId = suggestion.Id,
            Kind = deviations == SuggestionDeviation.None
                ? SuggestionDispositionKind.Taken
                : SuggestionDispositionKind.Modified,
            Reasons = SuggestionPassReason.None, // reasons are a pass concern; a take carries none
            Deviations = deviations,
            TakenEntryPrice = submittedEntry,
            TakenStopPrice = submittedStop,
            TakenTargetPrice = submittedTarget,
            TakenSize = submittedSize,
            CreatedAt = now,
        };
    }
}
