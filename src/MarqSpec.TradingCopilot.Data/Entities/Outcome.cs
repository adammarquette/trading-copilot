using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The resolution of a suggestion or trade — the journal's outcome record (data dictionary §7, R-9/R-15). It scores
/// what a suggestion or trade finally came to, and carries the three <b>independent</b> R-15 removal flags.
/// </summary>
/// <remarks>
/// <para>
/// <b>The R-15 flags are set only through this type's methods</b>, never by an external assignment — the setters are
/// private. That is the point: R-15 turns on training-exclusion and display-visibility being <i>independent</i>
/// controls with soft-delete as their combined shortcut, and a model that let a caller flip <see cref="Deleted"/>
/// without <see cref="TrainingExcluded"/> and <see cref="HiddenFromUser"/> could re-collapse the very distinction the
/// requirement exists to keep. <see cref="SoftDelete"/> sets all three atomically; the independent toggles move one
/// at a time.
/// </para>
/// <para>
/// The calibration pair (<see cref="PredictedRewardRisk"/> / <see cref="RealizedRewardRisk"/>) is a nullable seam:
/// this card stores it; the untaken-suggestion simulation and calibration reporting that populate it are [J2]/[J3]
/// (gh#832 <i>Not this card</i>). Hard delete + its audit fact, and the write path that composes outcomes from the
/// journal, are the paired I/O follow-ons.
/// </para>
/// </remarks>
public class Outcome : IUserOwned
{
    /// <summary>The outcome's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The trade this outcome resolves, when one was taken; null for an untaken-suggestion outcome.</summary>
    public Guid? TradeId { get; set; }

    /// <summary>The suggestion this outcome scores, when there was one; null for a manual trade with no suggestion.</summary>
    public Guid? SuggestionId { get; set; }

    /// <summary>How it resolved.</summary>
    public required OutcomeResolution Resolution { get; set; }

    /// <summary>Whether this outcome is a simulation of an untaken suggestion rather than a taken trade.</summary>
    public bool Simulated { get; set; }

    /// <summary>The predicted reward:risk — the calibration pair's predicted half; null until populated.</summary>
    public decimal? PredictedRewardRisk { get; set; }

    /// <summary>The realized reward:risk — the calibration pair's realized half; null until populated.</summary>
    public decimal? RealizedRewardRisk { get; set; }

    /// <summary>Excluded from the AI learning set (R-15).</summary>
    public bool TrainingExcluded { get; private set; }

    /// <summary>Hidden from default journal / report views (R-15).</summary>
    public bool HiddenFromUser { get; private set; }

    /// <summary>Soft-deleted (R-15).</summary>
    public bool Deleted { get; private set; }

    /// <summary>
    /// Soft-delete (R-15) — the default, reversible removal. Excludes the row from training and hides it from
    /// default views <b>together</b>, retaining the full record. Reverse with <see cref="Restore"/>.
    /// </summary>
    public void SoftDelete()
    {
        Deleted = true;
        TrainingExcluded = true;
        HiddenFromUser = true;
    }

    /// <summary>
    /// Reverse a <see cref="SoftDelete"/> (R-15) — restores the row to both the learning set and default views.
    /// </summary>
    public void Restore()
    {
        Deleted = false;
        TrainingExcluded = false;
        HiddenFromUser = false;
    }

    /// <summary>
    /// Include or exclude this row from the AI learning set (R-15), independently of its visibility — a loss can be
    /// excluded from training while staying visible for review.
    /// </summary>
    public void SetTrainingExcluded(bool excluded) => TrainingExcluded = excluded;

    /// <summary>
    /// Show or hide this row in default journal / report views (R-15), independently of whether it trains.
    /// </summary>
    public void SetHiddenFromUser(bool hidden) => HiddenFromUser = hidden;
}
