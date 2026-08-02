namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// What the operator did with a <see cref="Suggestion"/> — an <b>operator act only</b> (data dictionary §6,
/// vocabulary settled by gh#539): <see cref="Taken"/> / <see cref="Modified"/> / <see cref="Passed"/>.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> a lifecycle state: <c>active</c> / <c>stale</c> / <c>expired-void</c> are the clock's and
/// the market's (see <see cref="SuggestionState"/>), and <c>expired</c> is therefore <b>not</b> a disposition — a
/// system timeout is not an operator act, so nothing forges one. <see cref="Unknown"/> is the refusable zero (the
/// <c>CK_Suggestions_State_NotUnknown</c> fail-closed-zero pattern, gh#60); a DB check rejects it.
/// <para>
/// This card (gh#547) writes only <see cref="Passed"/> — the one disposition that needs nothing from execution.
/// <see cref="Taken"/> and <see cref="Modified"/> are the take-path's to write (gh#549); the vocabulary is minted
/// once here so those cards do not each declare a different enum.
/// </para>
/// </remarks>
public enum SuggestionDispositionKind
{
    /// <summary>Not a disposition — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>The operator took the suggestion as proposed (written by the take path, gh#549).</summary>
    Taken = 1,

    /// <summary>The operator took the suggestion with deviations (written by the take path, gh#549).</summary>
    Modified = 2,

    /// <summary>The operator declined the suggestion — a <b>neutral</b> pass, with optional structured reasons and a note.</summary>
    Passed = 3,
}
