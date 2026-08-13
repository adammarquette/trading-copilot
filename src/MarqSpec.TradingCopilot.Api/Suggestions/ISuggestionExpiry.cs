using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// The guarded, one-way <b>expire</b> transition for suggestions (gh#545, ADR-0013): a database-evaluated
/// conditional UPDATE that moves every <b>live</b> suggestion whose validity window has passed to
/// <see cref="SuggestionState.ExpiredVoid"/>, stamping <c>StateChangedAt</c> in the <b>same write</b>. Both the
/// steady-state sweep and the startup recovery pass drive from it, so recovery and normal operation cannot diverge.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-way and guarded on the prior state.</b> After the suggestion engine lands there are four writers to
/// <c>Suggestion.State</c> on different clocks (this expire sweep, the drift consumer gh#546, the superseder gh#550,
/// and the take path). The transition is predicated on the prior state — <c>WHERE State IN (Active, Stale)</c> — so
/// it is <b>monotonic</b> (Active → Stale → ExpiredVoid, never backward) and a concurrent competing update cannot
/// silently clobber it: Postgres row locks serialize the two, and each re-evaluates its own prior-state guard
/// against the committed row. Deliberately <b>not</b> an entity-wide EF concurrency token — a global token is
/// symmetric and would change <c>SaveChanges</c> failure semantics for every writer on the table (the gh#183
/// lesson). The predicate mirrors <see cref="SuggestionLifecycle.Decide"/> — a live suggestion past its window —
/// which is the unit-tested semantic authority.
/// </para>
/// <para>
/// <b>Why a seam.</b> The decision must be evaluated by the database (a background sweep and a request-scoped writer
/// share no change tracker), so it is <c>ExecuteUpdate</c> — which the EF in-memory provider does not support (the
/// gh#530 lesson). The seam lets the sweep host and the startup path be unit-tested against a fake, and puts the
/// real compare-and-swap where its atomicity can be proven: the container-backed Postgres tier (QA, gh#552).
/// </para>
/// </remarks>
public interface ISuggestionExpiry
{
    /// <summary>
    /// Expires every live suggestion whose validity window has passed as of <paramref name="now"/> — <b>across all
    /// owners</b>. Background plumbing has no request user, so it bypasses the R-20 filter; the transition is
    /// owner-agnostic (a suggestion expires on its own <c>ExpiresAt</c> alone), exactly as the rehydrator's
    /// cross-owner count is, so no per-owner scoping is needed and none is done.
    /// </summary>
    /// <param name="now">The current time, supplied by the caller — the decision never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The suggestions this call expired — each with its id and owner (gh#718), so the caller can push the
    /// <see cref="SuggestionState.ExpiredVoid"/> transition to each owner's realtime connections. Empty when nothing
    /// was due; the count is the list length, for the callers that log it.
    /// </returns>
    Task<IReadOnlyList<SuggestionTransition>> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The database-evaluated expire transition — a single guarded conditional UPDATE (gh#545).</summary>
public sealed class SuggestionExpiry : ISuggestionExpiry
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the expiry over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public SuggestionExpiry(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SuggestionTransition>> ExpireDueAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<Suggestion> due = _database.Suggestions
            .IgnoreQueryFilters()
            .Where(suggestion =>
                (suggestion.State == SuggestionState.Active || suggestion.State == SuggestionState.Stale)
                && suggestion.ExpiresAt <= now);

        // Read the (id, owner) of the rows about to transition, THEN run the guarded UPDATE, in ONE transaction so the
        // recovered set is exactly what the UPDATE changes (gh#718) -- ExecuteUpdate cannot RETURN the rows. The
        // prior-state guard on the UPDATE is unchanged, so the gh#545 monotonicity and the single-write StateChangedAt
        // stamp do not regress; the SELECT adds only the ids the per-owner push needs. Both statements are relational
        // (the in-memory provider runs neither -- gh#530), so this stays off any in-memory path exactly as before, and
        // the compare-and-swap's atomicity is proven on Postgres by QA.
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);

        List<SuggestionTransition> transitioned = await due
            .Select(suggestion => new SuggestionTransition(suggestion.Id, suggestion.UserId))
            .ToListAsync(cancellationToken);

        await due.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(suggestion => suggestion.State, SuggestionState.ExpiredVoid)
                .SetProperty(suggestion => suggestion.StateChangedAt, now),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return transitioned;
    }
}
