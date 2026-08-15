using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Data.Journal;

/// <summary>
/// The R-15 read semantics for <see cref="Outcome"/> — composable query filters that honour the removal flags, and
/// can be asked not to (gh#832). Kept as <see cref="IQueryable{T}"/> extensions so they translate to SQL and compose
/// into whatever a reader or report projects.
/// </summary>
public static class OutcomeQueries
{
    /// <summary>
    /// Omit soft-deleted rows (R-15) — the <b>soft-delete dimension only</b>, the primitive the report toggle is
    /// built on. This is <b>not</b> the full default journal view: that also hides <c>hidden_from_user</c> rows (see
    /// <see cref="Visible"/>). Kept separate so a report can re-include soft-deleted rows independently of visibility.
    /// </summary>
    public static IQueryable<Outcome> ExcludingDeleted(this IQueryable<Outcome> outcomes) =>
        outcomes.Where(outcome => !outcome.Deleted);

    /// <summary>
    /// Toggle inclusive vs. exclusive of soft-deleted rows (R-15) — so a report can render the same period
    /// <b>with and without</b> soft-deleted records from one call, keeping the honest picture recoverable.
    /// <paramref name="include"/> <see langword="false"/> is exactly <see cref="ExcludingDeleted"/>.
    /// </summary>
    public static IQueryable<Outcome> IncludingDeleted(this IQueryable<Outcome> outcomes, bool include) =>
        include ? outcomes : outcomes.ExcludingDeleted();

    /// <summary>
    /// The default journal / report view (R-15) — omits <b>both</b> soft-deleted and <c>hidden_from_user</c> rows, so
    /// a row the operator hid (without excluding it from training) does not surface in the default view, and a
    /// soft-deleted row (which is also hidden) never does either.
    /// </summary>
    public static IQueryable<Outcome> Visible(this IQueryable<Outcome> outcomes) =>
        outcomes.Where(outcome => !outcome.Deleted && !outcome.HiddenFromUser);

    /// <summary>
    /// The AI learning set (R-15) — drops <c>training_excluded</c> <b>and</b> soft-deleted rows. Soft-delete excludes
    /// a record from all training, and the flags are independently mutable, so this cannot rely on soft-delete having
    /// left <c>training_excluded</c> set: a row soft-deleted and then re-enabled for training must still be dropped.
    /// </summary>
    public static IQueryable<Outcome> ForTrainingSignal(this IQueryable<Outcome> outcomes) =>
        outcomes.Where(outcome => !outcome.TrainingExcluded && !outcome.Deleted);
}
