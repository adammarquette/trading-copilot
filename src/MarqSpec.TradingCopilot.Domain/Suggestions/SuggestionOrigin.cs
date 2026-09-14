namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// <b>Which producer staged a suggestion</b> (gh#1134; data dictionary §6) — the trigger scan, or the chat co-pilot's
/// <c>generate_suggestion</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// It exists because gh#1134 gave <c>Suggestion</c> a <b>second producer</b>, and the operator's card must be able to
/// say which one it is looking at. Until then the scan was the only writer, so provenance could be <i>inferred</i>
/// from <c>TriggerFiringId</c> — but that inference is already overloaded: an empty <c>CitedFactors</c> set means both
/// "this proposal cites no signal" and "the read forgot to <c>Include</c> the set", and a card cannot tell those
/// apart. A producer is a <b>fact of the row</b>, so it is stored as one rather than reconstructed from the absence of
/// something else, and a third producer later joins the enum instead of silently landing in chat's bucket.
/// </para>
/// <para>
/// <see cref="Unknown"/> is the refusable zero — a DB check rejects it (the fail-closed-zero pattern, gh#60) — so a
/// row whose producer nobody set is refused rather than defaulting into a producer it did not come from. Rows that
/// predate gh#1134 backfill to <see cref="Scan"/>, which is what they historically were.
/// </para>
/// <para>
/// It is <b>provenance, not permission</b>: nothing about the execution path reads it. A chat-originated suggestion is
/// taken through the identical operator take and the identical risk gate below the model (R-11 / R-5). The one place
/// it changes behaviour is the R-4 issuance throttle, which counts only <see cref="Scan"/> rows — that cap governs
/// what the scan issues <i>unprompted</i>, not what the operator asked the co-pilot for.
/// </para>
/// </remarks>
public enum SuggestionOrigin
{
    /// <summary>Not a producer — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>The trigger scan's agent review staged it from a fired trigger (R-4).</summary>
    Scan = 1,

    /// <summary>The chat co-pilot's <c>generate_suggestion</c> tool staged it on the operator's request (R-6, gh#1134).</summary>
    Chat = 2,
}
