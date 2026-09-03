using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// Operator (or, in future, AI) feedback attached to a closed <see cref="Trade"/> (data dictionary §7, R-8, gh#1064)
/// — optional, asynchronous annotation that never blocks or participates in the close path (the gh#289 lesson: a
/// journal write on a hot path is a defect). A trade may carry several entries over time — the ERD's
/// <c>Trade ||--o{ TradeFeedback</c> — because feedback "can be added anytime", not just once.
/// </summary>
/// <remarks>
/// <b>"Awaiting review" is not a column here or on <see cref="Trade"/>.</b> It is derived by
/// <see cref="TradeReviewPolicy"/> from whether any row exists for a trade with <see cref="Author"/> ==
/// <see cref="FeedbackAuthor.Operator"/>, so the flag can never drift from what feedback actually exists (the same
/// reasoning that keeps <c>Outcome</c>'s R-15 flags off a second, driftable source of truth).
/// </remarks>
public class TradeFeedback : IUserOwned
{
    /// <summary>The longest a <see cref="Comment"/> may be. Shared by the DB config and the endpoint guard.</summary>
    public const int CommentMaxLength = 1000;

    /// <summary>The longest an <see cref="EmotionalState"/> label may be. Shared by the DB config and the endpoint guard.</summary>
    public const int EmotionalStateMaxLength = 64;

    /// <summary>The feedback row's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — always the trade's owner. Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The trade this feedback annotates.</summary>
    public required Guid TradeId { get; set; }

    /// <summary>An optional free-text note, capped at <see cref="CommentMaxLength"/>. <see langword="null"/> when omitted.</summary>
    public string? Comment { get; set; }

    /// <summary>Optional free-form labels the operator attaches (e.g. a setup or a mistake tag). Empty when none.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// An optional short label for the operator's state of mind while taking or managing the trade, capped at
    /// <see cref="EmotionalStateMaxLength"/>. Deliberately free text rather than an enum: neither the PRD nor the
    /// data dictionary defines a fixed vocabulary, and the journal blotter UI that would drive a fixed picker is
    /// not yet built (R-8) — constraining this now would mean inventing an unreviewed taxonomy.
    /// </summary>
    public string? EmotionalState { get; set; }

    /// <summary>Who wrote this entry. Never the unset zero — refused by <c>CK_TradeFeedback_Author_NotUnknown</c>.</summary>
    public required FeedbackAuthor Author { get; set; }

    /// <summary>When this feedback was recorded.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
