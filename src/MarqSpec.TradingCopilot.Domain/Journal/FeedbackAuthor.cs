namespace MarqSpec.TradingCopilot.Domain.Journal;

/// <summary>
/// Who authored a <c>TradeFeedback</c> row (data dictionary §7, R-8). Distinguishes the operator's own review from
/// a system/AI-generated note, so <see cref="TradeReviewPolicy"/> can require the <b>operator's</b> attention
/// specifically — an AI note alone must never clear "awaiting review".
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the unset zero and is refused by a DB check (<c>CK_TradeFeedback_Author_NotUnknown</c>),
/// the same posture <c>OutcomeResolution</c> takes. This card (gh#1064) only ever writes <see cref="Operator"/>;
/// <see cref="Ai"/> is a documented seam for a future automated-feedback writer (R-6) — not built here.
/// </remarks>
public enum FeedbackAuthor
{
    /// <summary>The unset zero — never a stored value; refused by <c>CK_TradeFeedback_Author_NotUnknown</c>.</summary>
    Unknown = 0,

    /// <summary>The operator wrote this feedback.</summary>
    Operator = 1,

    /// <summary>A future automated / AI-generated note. No writer produces this yet.</summary>
    Ai = 2,
}
