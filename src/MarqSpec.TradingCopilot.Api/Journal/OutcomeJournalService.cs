using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>
/// Composes an <see cref="Outcome"/> for every closed <see cref="Trade"/> that does not yet have one (gh#909, R-9) —
/// the I/O half of the model gh#832 landed. The journal already holds the terminal fact (a closed round trip with a
/// signed <c>RealizedPnL</c>), so this is a projection over it: <see cref="OutcomeResolutionPolicy"/> turns the sign
/// into a <see cref="OutcomeResolution"/> (win / loss / break-even scratch) and the row is written owner-scoped, so
/// the R-15 report surface and the calibration / expectancy readers (gh#21 / gh#22) have outcomes to read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent by construction</b>, exactly as the <c>TradeJournalService</c> it mirrors: a trade already carrying
/// an outcome is skipped by the anti-join, and the unique filtered index on <c>Outcome.TradeId</c> (gh#910) is the
/// database backstop against a concurrent double-compose — a replay recomposes the same row and the second insert is
/// rejected, so one trade can never mint two outcomes (one Win, one Loss).
/// </para>
/// <para>
/// <b>Cross-owner sweep.</b> It runs from a background host that has no request user, so it reads across owners with
/// <c>IgnoreQueryFilters</c> and stamps each outcome's <c>UserId</c> from its own trade (the R-20 filter scopes reads
/// but does not stamp inserts). <b>Refuse-don't-guess</b> survives from the policy: a closed trade carrying no signed
/// result is left un-outcomed rather than scored a guess. A second sweep composes outcomes for terminal <b>unfilled
/// suggestions</b> (gh#939) — an expired-void or passed suggestion that never became a trade — via the same
/// idempotent, cross-owner shape, backstopped by the unique filtered index on <c>Outcome.SuggestionId</c>.
/// </para>
/// </remarks>
public sealed class OutcomeJournalService
{
    /// <summary>The most trades one pass composes, so a large backlog drains over several bounded passes.</summary>
    private const int MaxPerPass = 500;

    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<OutcomeJournalService> _logger;

    /// <summary>Creates the service over the (host-scoped) database.</summary>
    /// <param name="database">The database; the sweep reads across owners and stamps writes from each trade.</param>
    /// <param name="logger">The logger.</param>
    public OutcomeJournalService(TradingCopilotDbContext database, ILogger<OutcomeJournalService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Composes outcomes for up to a bounded batch of closed trades that lack one, oldest close first.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The number of outcomes written this pass.</returns>
    public async Task<int> ComposeClosedTradeOutcomesAsync(CancellationToken cancellationToken)
    {
        // The trade ids that already carry an outcome — so the sweep is an anti-join, not a re-scan. Materialized so
        // the trade query is a simple NOT IN on both providers (the EF in-memory provider does not translate a
        // correlated sub-query the same way Postgres does). Cross-owner: no request user, so IgnoreQueryFilters.
        List<Guid> alreadyOutcomed = await _database.Outcomes
            .IgnoreQueryFilters()
            .Where(outcome => outcome.TradeId != null)
            .Select(outcome => outcome.TradeId!.Value)
            .ToListAsync(cancellationToken);

        // A hard-deleted outcome leaves a recomposition-suppression tombstone keyed on its trade (gh#955) — anti-join
        // it too, so the sweep never re-derives an outcome the operator confirmed removing. Cross-owner: IgnoreQueryFilters.
        List<Guid> suppressed = await _database.OutcomeSuppressions
            .IgnoreQueryFilters()
            .Where(suppression => suppression.TradeId != null)
            .Select(suppression => suppression.TradeId!.Value)
            .ToListAsync(cancellationToken);

        List<Trade> pending = await _database.Trades
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(trade => trade.ClosedAt != null && trade.RealizedPnL != null
                && !alreadyOutcomed.Contains(trade.Id) && !suppressed.Contains(trade.Id))
            .OrderBy(trade => trade.ClosedAt)
            .Take(MaxPerPass)
            .ToListAsync(cancellationToken);

        int written = 0;
        foreach (Trade trade in pending)
        {
            if (await TryComposeClosedTradeAsync(trade, cancellationToken))
            {
                written++;
            }
        }

        if (written > 0)
        {
            _logger.LogInformation("Composed {Count} trade outcome(s).", written);
        }

        return written;
    }

    private async Task<bool> TryComposeClosedTradeAsync(Trade trade, CancellationToken cancellationToken)
    {
        // Refuse-don't-guess: the policy declines a closed trade with no signed result. The sweep already filters
        // RealizedPnL != null, so this is the belt-and-suspenders that keeps an unresolvable row un-outcomed.
        if (!OutcomeResolutionPolicy.TryResolve(
            OutcomeBasis.ClosedTrade, trade.RealizedPnL, out OutcomeResolution resolution))
        {
            return false;
        }

        Outcome outcome = new()
        {
            Id = Guid.NewGuid(),
            UserId = trade.UserId, // R-20: the filter does not stamp inserts, so set the owner from the trade
            TradeId = trade.Id,
            SuggestionId = trade.SuggestionId, // carry the R-9 lineage; it survives suggestion deletion as null
            Resolution = resolution,
            Simulated = false, // a taken, closed trade is never a simulation (gh#832 [J2] owns simulated outcomes)
        };
        _database.Outcomes.Add(outcome);

        try
        {
            await _database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsOutcomeTradeKeyViolation(exception))
        {
            // A concurrent pass composed this trade's outcome first: the unique filtered index on TradeId rejected the
            // second insert — idempotent by construction. Detach the rejected row so it cannot re-flush on the next
            // trade's SaveChanges in this shared context (the TradeJournalService lesson).
            _database.Entry(outcome).State = EntityState.Detached;
            _logger.LogInformation(
                "Trade {Trade} was already outcomed by a concurrent pass; idempotent skip.", trade.Id);
            return false;
        }
    }

    /// <summary>
    /// Composes outcomes for up to a bounded batch of terminal <b>unfilled</b> suggestions that lack one (gh#939): a
    /// <see cref="SuggestionState.ExpiredVoid"/> suggestion that never filled → <see cref="OutcomeResolution.Expired"/>,
    /// and a <b>passed</b> suggestion → <see cref="OutcomeResolution.NoFillScratch"/>. A <b>taken</b> suggestion is
    /// skipped — its outcome comes from the closed trade (with a <c>TradeId</c>), never here.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The number of outcomes written this pass.</returns>
    public async Task<int> ComposeUnfilledSuggestionOutcomesAsync(CancellationToken cancellationToken)
    {
        // Suggestion ids that already carry an outcome — the anti-join (materialized, like the trade sweep). Cross-owner:
        // no request user, so IgnoreQueryFilters throughout and each write stamps its owner from the suggestion.
        List<Guid> alreadyOutcomed = await _database.Outcomes
            .IgnoreQueryFilters()
            .Where(outcome => outcome.SuggestionId != null)
            .Select(outcome => outcome.SuggestionId!.Value)
            .ToListAsync(cancellationToken);

        // A hard-deleted untaken outcome leaves a suppression tombstone keyed on its suggestion (gh#955) — anti-join it,
        // so the sweep does not re-derive an outcome the operator confirmed removing (the untaken mirror of the trade sweep).
        List<Guid> suppressed = await _database.OutcomeSuppressions
            .IgnoreQueryFilters()
            .Where(suppression => suppression.SuggestionId != null)
            .Select(suppression => suppression.SuggestionId!.Value)
            .ToListAsync(cancellationToken);

        // A TAKEN / MODIFIED suggestion produced a trade; its outcome is composed from the closed trade (with a
        // TradeId), never here — so it is excluded even if the suggestion itself later reads ExpiredVoid.
        List<Guid> taken = await _database.SuggestionDispositions
            .IgnoreQueryFilters()
            .Where(disposition => disposition.Kind == SuggestionDispositionKind.Taken
                || disposition.Kind == SuggestionDispositionKind.Modified)
            .Select(disposition => disposition.SuggestionId)
            .ToListAsync(cancellationToken);

        // A suggestion an ORDER was armed from is taken WHATEVER its disposition row says (gh#939 review): the take
        // path writes the disposition in a best-effort unit of work AFTER the order is Working and swallows faults,
        // and a pass/take race can leave a taken suggestion journaled Passed — so the disposition alone is not a
        // reliable "untaken" signal. Order.SuggestionId is the durable armed fact; a suggestion it names is excluded,
        // so the closed-trade sweep (once its position closes) is the one and only writer of that suggestion's
        // outcome — never a spurious untaken row alongside the eventual trade one.
        List<Guid> armed = await _database.Orders
            .IgnoreQueryFilters()
            .Where(order => order.SuggestionId != null)
            .Select(order => order.SuggestionId!.Value)
            .ToListAsync(cancellationToken);

        // A PASSED suggestion is a scratch — the operator's explicit decline, an actual resolution that wins over the
        // clock's expiry (see the basis pick below).
        List<Guid> passed = await _database.SuggestionDispositions
            .IgnoreQueryFilters()
            .Where(disposition => disposition.Kind == SuggestionDispositionKind.Passed)
            .Select(disposition => disposition.SuggestionId)
            .ToListAsync(cancellationToken);

        // Terminal + unfilled + not-yet-outcomed: a passed suggestion (whatever its clock state), or one the clock
        // expired void — but never a taken one.
        List<Suggestion> pending = await _database.Suggestions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(suggestion => !alreadyOutcomed.Contains(suggestion.Id)
                && !suppressed.Contains(suggestion.Id)
                && !taken.Contains(suggestion.Id)
                && !armed.Contains(suggestion.Id)
                && (passed.Contains(suggestion.Id) || suggestion.State == SuggestionState.ExpiredVoid))
            .OrderBy(suggestion => suggestion.CreatedAt)
            .Take(MaxPerPass)
            .ToListAsync(cancellationToken);

        HashSet<Guid> passedSet = [.. passed];
        int written = 0;
        foreach (Suggestion suggestion in pending)
        {
            // A pass (explicit decline) resolves to a scratch and wins over the clock's expiry.
            OutcomeBasis basis = passedSet.Contains(suggestion.Id)
                ? OutcomeBasis.Scratched
                : OutcomeBasis.ExpiredUnfilled;
            if (await TryComposeUnfilledSuggestionAsync(suggestion, basis, cancellationToken))
            {
                written++;
            }
        }

        if (written > 0)
        {
            _logger.LogInformation("Composed {Count} unfilled-suggestion outcome(s).", written);
        }

        return written;
    }

    private async Task<bool> TryComposeUnfilledSuggestionAsync(
        Suggestion suggestion, OutcomeBasis basis, CancellationToken cancellationToken)
    {
        // The policy maps ExpiredUnfilled → Expired and Scratched → NoFillScratch — there is no realized P&L for an
        // unfilled suggestion. It never declines these two bases, so a false here would be a defect, not a skip.
        if (!OutcomeResolutionPolicy.TryResolve(basis, realizedPnL: null, out OutcomeResolution resolution))
        {
            return false;
        }

        Outcome outcome = new()
        {
            Id = Guid.NewGuid(),
            UserId = suggestion.UserId, // R-20: stamp the owner from the suggestion (the filter does not stamp inserts)
            TradeId = null,             // untaken — no trade
            SuggestionId = suggestion.Id,
            Resolution = resolution,
            Simulated = false,          // a real terminal disposition, never a counterfactual simulation (gh#832 [J2])
        };
        _database.Outcomes.Add(outcome);

        try
        {
            await _database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsOutcomeSuggestionKeyViolation(exception))
        {
            // A concurrent pass composed this suggestion's outcome first: the unique filtered index on SuggestionId
            // rejected the second insert — idempotent by construction, the untaken path's backstop (mirrors TradeId).
            _database.Entry(outcome).State = EntityState.Detached;
            _logger.LogInformation(
                "Suggestion {Suggestion} was already outcomed by a concurrent pass; idempotent skip.", suggestion.Id);
            return false;
        }
    }

    /// <summary>
    /// The unique filtered index on <c>Outcome.TradeId</c> (gh#910) — one outcome per trade — and the <b>only</b>
    /// violation this writer treats as the benign idempotent replay; any other write fault propagates to the host's
    /// pass-level guard. Pinned to the index's real name by a model-metadata test, so a rename cannot silently demote
    /// this catch-side backstop (the gh#747 posture). The EF in-memory provider throws a bare
    /// <see cref="DbUpdateException"/> with no <see cref="PostgresException"/> inner, so it never matches here — the
    /// anti-join above is the in-memory idempotency, and QA proves the concurrent race against real Postgres.
    /// </summary>
    internal const string OutcomeTradeKeyIndex = "IX_Outcomes_TradeId";

    /// <summary>Whether a write fault is the <see cref="OutcomeTradeKeyIndex"/> unique violation — and only that.</summary>
    /// <param name="exception">The write fault <c>SaveChanges</c> raised.</param>
    /// <returns><see langword="true"/> only for the outcome-per-trade unique violation.</returns>
    internal static bool IsOutcomeTradeKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OutcomeTradeKeyIndex,
        };
    }

    /// <summary>
    /// The unique filtered index on <c>Outcome.SuggestionId</c> (gh#939) — one <b>untaken</b> outcome per suggestion —
    /// the untaken path's idempotency backstop, mirroring <see cref="OutcomeTradeKeyIndex"/>. Filtered to
    /// <c>SuggestionId IS NOT NULL AND TradeId IS NULL</c>: a <i>taken</i> suggestion can produce several trade legs
    /// (gh#759), each a trade-derived outcome carrying the same <c>SuggestionId</c>, so uniqueness applies only to the
    /// no-trade rows. Pinned to the model's real name by a metadata test (the gh#747 posture).
    /// </summary>
    internal const string OutcomeSuggestionKeyIndex = "IX_Outcomes_SuggestionId";

    /// <summary>Whether a write fault is the <see cref="OutcomeSuggestionKeyIndex"/> unique violation — and only that.</summary>
    /// <param name="exception">The write fault <c>SaveChanges</c> raised.</param>
    /// <returns><see langword="true"/> only for the untaken-outcome-per-suggestion unique violation.</returns>
    internal static bool IsOutcomeSuggestionKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OutcomeSuggestionKeyIndex,
        };
    }
}
