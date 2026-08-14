using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// The guarded, one-way <b>drift</b> transition for suggestions (gh#546, R-4 / R-12, ADR-0013): a database-evaluated
/// conditional UPDATE that moves every <b>Active</b> suggestion on one instrument whose price has drifted past the
/// entry tolerance to <see cref="SuggestionState.Stale"/>, stamping <c>StateChangedAt</c> in the <b>same write</b>.
/// The sibling of <see cref="ISuggestionExpiry"/> — the drift consumer named in its doc as the second concurrent
/// writer to <c>Suggestion.State</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-way and guarded on the prior state.</b> The predicate is <c>WHERE State == Active</c> — <b>only</b> Active,
/// not the expire sweep's <c>IN (Active, Stale)</c>. That makes it <b>monotonic</b> (Active → Stale, and re-processing
/// an already-Stale row matches nothing, so it is idempotent) and race-free against the sweep: Postgres row locks
/// serialize the two, each re-evaluates its own prior-state guard against the committed row, and either order stays
/// forward-only (Active → Stale → ExpiredVoid). A returned price can never <b>un-stale</b> a row — there is no
/// Stale → Active transition here or anywhere (ADR-0013's <i>"a scratched setup is not chased"</i>); a re-formed setup
/// is a new superseding suggestion (gh#550). Deliberately <b>not</b> an entity-wide EF concurrency token — a global
/// token is symmetric and would change <c>SaveChanges</c> failure semantics for every writer on the table (the gh#183
/// lesson), exactly as <see cref="ISuggestionExpiry"/> notes.
/// </para>
/// <para>
/// <b>Why a seam.</b> The decision must be evaluated by the database (a background quote consumer shares no change
/// tracker with anything), so it is <c>ExecuteUpdate</c> — which the EF in-memory provider does not support (the
/// gh#530 lesson). The seam lets the drift service be unit-tested against a faithful in-memory double, and puts the
/// real compare-and-swap where its atomicity can be proven: the container-backed Postgres tier (QA, gh#614). The
/// bid/ask/band bounds are <b>caller-computed decimals</b> (the tick-size-scaled band is resolved from the instrument
/// spec by the caller, since a config-backed tick size cannot be joined in SQL), so the predicate is entirely
/// column-vs-scalar and fully translatable. It mirrors <see cref="Domain.Suggestions.SuggestionLifecycle.HasDrifted"/>
/// — the unit-tested semantic authority — expressed without <c>Math.Abs</c> as two symmetric bounds.
/// </para>
/// </remarks>
public interface ISuggestionDrift
{
    /// <summary>
    /// Marks every <b>Active</b> suggestion on <paramref name="instrument"/> whose entry has drifted more than
    /// <paramref name="band"/> from the achievable price (ask for a long, bid for a short) <see cref="SuggestionState.Stale"/>,
    /// across <b>all owners</b> — a background consumer has no request user, and drift is owner-agnostic (a suggestion
    /// drifts on its own geometry alone), so it bypasses the R-20 filter exactly as the expire sweep does.
    /// </summary>
    /// <param name="instrument">The venue-neutral symbol (e.g. <c>ES</c>) whose suggestions this quote drift-checks.</param>
    /// <param name="bid">The current best bid — a short's achievable entry.</param>
    /// <param name="ask">The current best ask — a long's achievable entry.</param>
    /// <param name="band">The drift tolerance as a price distance (ticks × tick size), computed by the caller.</param>
    /// <param name="now">The current time, supplied by the caller — the transition never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The suggestions this call moved to <see cref="SuggestionState.Stale"/> — each with its id and owner (gh#718),
    /// so the caller can push the transition to each owner's realtime connections. Empty when none drifted; the count
    /// is the list length, for the callers that log it.
    /// </returns>
    Task<IReadOnlyList<SuggestionTransition>> MarkDriftedStaleAsync(
        string instrument, decimal bid, decimal ask, decimal band, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The database-evaluated drift transition — a single guarded conditional UPDATE (gh#546).</summary>
public sealed class SuggestionDrift : ISuggestionDrift
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the drift writer over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public SuggestionDrift(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SuggestionTransition>> MarkDriftedStaleAsync(
        string instrument, decimal bid, decimal ask, decimal band, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<Suggestion> drifted = _database.Suggestions
            .IgnoreQueryFilters()
            .Where(suggestion =>
                suggestion.State == SuggestionState.Active
                && suggestion.Instrument == instrument
                // |price - entry| > band, expressed as two symmetric bounds (no Math.Abs) so it translates to SQL;
                // a long measures against the ask (what it pays), a short against the bid (what it receives).
                && ((suggestion.Side == OrderSide.Buy
                        && (suggestion.EntryPrice < ask - band || suggestion.EntryPrice > ask + band))
                    || (suggestion.Side == OrderSide.Sell
                        && (suggestion.EntryPrice < bid - band || suggestion.EntryPrice > bid + band))));

        // Run the guarded UPDATE, then recover the rows it ACTUALLY changed by their write fingerprint -- the
        // (StateChangedAt == now, State == Stale, Instrument) triple this UPDATE just stamped (gh#718, #824 review).
        // ExecuteUpdate cannot RETURN the changed rows, and a PRE-read then update could drift from the update under
        // READ COMMITTED (a concurrent expire / supersede voiding a row between the two would leave the pre-read
        // pushing Stale for a row now ExpiredVoid). Recovering by the stamp instead pushes EXACTLY what this UPDATE
        // transitioned, in either race direction: `now` is this pass's fresh UtcNow, so the fingerprint cannot collide
        // with another writer's stamp, and the target-state + instrument narrow it to this call. The Active-only guard
        // is unchanged, so the gh#546 monotonicity does not regress. Both statements are relational (the in-memory
        // provider runs neither -- gh#530), so the atomicity is proven on Postgres by QA.
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);

        int changed = await drifted.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(suggestion => suggestion.State, SuggestionState.Stale)
                .SetProperty(suggestion => suggestion.StateChangedAt, now),
            cancellationToken);

        List<SuggestionTransition> transitioned = changed == 0
            ? []
            : await _database.Suggestions
                .IgnoreQueryFilters()
                .Where(suggestion => suggestion.Instrument == instrument
                    && suggestion.State == SuggestionState.Stale
                    && suggestion.StateChangedAt == now)
                .Select(suggestion => new SuggestionTransition(suggestion.Id, suggestion.UserId))
                .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return transitioned;
    }
}
