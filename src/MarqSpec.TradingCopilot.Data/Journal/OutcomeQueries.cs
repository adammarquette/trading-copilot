using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Data.Journal;

/// <summary>
/// The R-15 read semantics for <see cref="Outcome"/> — composable query filters that honour the removal flags, and
/// can be asked not to (gh#832). Kept as <see cref="IQueryable{T}"/> extensions so they translate to SQL and compose
/// into whatever a reader or report projects.
/// </summary>
public static class OutcomeQueries
{
    /// <summary>The default view / default stats — soft-deleted rows are omitted (R-15).</summary>
    public static IQueryable<Outcome> ExcludingDeleted(this IQueryable<Outcome> outcomes) =>
        outcomes.Where(outcome => !outcome.Deleted);

    /// <summary>
    /// Toggle between the default view and the inclusive figure (R-15) — so a report can render the same period
    /// <b>with and without</b> soft-deleted records from one call, keeping the honest picture recoverable.
    /// <paramref name="include"/> <see langword="false"/> is exactly <see cref="ExcludingDeleted"/>.
    /// </summary>
    public static IQueryable<Outcome> IncludingDeleted(this IQueryable<Outcome> outcomes, bool include) =>
        include ? outcomes : outcomes.ExcludingDeleted();

    /// <summary>
    /// The AI learning set — training-excluded rows are dropped (R-15). Soft-delete implies training-exclusion, so a
    /// soft-deleted row is dropped here too; a row excluded-but-visible is dropped from training while remaining in
    /// the default view.
    /// </summary>
    public static IQueryable<Outcome> ForTrainingSignal(this IQueryable<Outcome> outcomes) =>
        outcomes.Where(outcome => !outcome.TrainingExcluded);
}
