using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Journal;
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
/// result is left un-outcomed rather than scored a guess. Only the closed-trade path lands here; composing outcomes
/// for terminal <b>unfilled suggestions</b> is the paired follow-on on this card.
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

        List<Trade> pending = await _database.Trades
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(trade => trade.ClosedAt != null && trade.RealizedPnL != null && !alreadyOutcomed.Contains(trade.Id))
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
}
