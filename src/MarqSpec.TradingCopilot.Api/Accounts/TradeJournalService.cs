using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// The production <see cref="Trade"/> writer (gh#731, R-8/R-9): when a position goes <b>flat</b>, reconstruct the
/// round trip from its fills and journal it with a signed, tick-value-aware <c>RealizedPnL</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="Trade"/> is the journal spine (gh#7) and two readers already consume it —
/// <c>DailyRealizedReader</c> (the daily-headroom read, gh#587) and <c>ConsistencyWindowReader</c> (the send-path
/// consistency gate). Both pick a row up the moment <b>both</b> <c>ClosedAt</c> and <c>RealizedPnL</c> are
/// non-null. Until this writer existed nothing wrote <c>Trade</c> in production, so both read <b>zero forever</b>
/// and the R-4 throttle (gh#551) had nothing to throttle on.
/// </para>
/// <para>
/// <b>The trigger</b> is the same flat signal <see cref="OcoExitService"/> uses — a <see cref="PositionEvent"/>
/// with <c>NetQuantity == 0</c> — because it is the unambiguous "round trip closed" instant and every exit route
/// (manual flatten, bracket stop/target, auto-flatten, kill switch) ends in one.
/// </para>
/// <para>
/// <b>Venue truth, not app belief.</b> The round trip is composed from the <see cref="Fill"/> rows the account-event
/// ingestion recorded — what actually executed — never from what the app believes it sent. Point value comes from
/// the snapshotted <c>Order.PointValue</c> (a fact of placement), and <c>Mode</c> is copied from the order, never
/// from the account's current declaration: a trade closes after placement, when the declaration may legitimately
/// have moved on, and practice results must never blend into live results (R-14).
/// </para>
/// <para>
/// <b>Idempotent by construction.</b> Each leg's <c>(ClosingFillId, OpeningFillId)</c> pair is its natural key, behind a unique index — a
/// replayed flat event recomposes the same round trip and the second insert is rejected, exactly as the fill
/// consumer dedupes on <c>{ OrderId, VenueFillKey }</c>. This matters more than tidiness: a double-written trade
/// would double-count the day's realized P&amp;L into the daily governor and mis-state the operator's headroom.
/// </para>
/// <para>
/// <b>Per-leg composition (gh#759, ADR-0022).</b> No longer only the balanced single <b>enter → exit → flat</b> round trip is journalled;
/// scale-in with a partial exit and stop-and-reverse now <b>journal</b> per opening leg (a spanning exit is split
/// across the legs it retires), their realized P&amp;L reaching the governor. Only genuine ambiguity writes nothing,
/// the honest outcome — a wrong <c>RealizedPnL</c> feeding
/// the governor is worse than a missing one, which is visibly zero.
/// </para>
/// </remarks>
public sealed class TradeJournalService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<TradeJournalService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to resolve the owning account (across owners).</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="metrics">The execution-SLI sink — every flat's journaling outcome is counted through it, so an
    /// account stuck refusing is visible to an alert, not only to a log (gh#731, gh#734 review).</param>
    /// <param name="logger">The logger.</param>
    public TradeJournalService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IExecutionMetrics metrics,
        ILogger<TradeJournalService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _discovery = discovery;
        _options = options;
        _projectX = projectXOptions.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Journals the round trip a now-flat position completed.</summary>
    /// <param name="exit">The position-update event — acted on only when it reports the contract flat.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when a trade was journalled.</returns>
    public async Task<bool> ProcessFlatAsync(PositionEvent exit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exit);

        // Only a FLAT contract closes a round trip. A partial fill leaves the net non-zero: the position is still
        // live and its outcome is not yet known.
        if (exit.NetQuantity != 0)
        {
            return false;
        }

        JournalAccount? account = await ResolveAccountAsync(exit.Account, cancellationToken);
        if (account is null)
        {
            _logger.LogDebug("Flat position for unrecognized account {Account}; nothing journalled.", exit.Account);
            return false;
        }

        // Per-owner context: every read and the write below are R-20-scoped to the account's owner.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(account.UserId));

        // The orders of ours on the contract -- deliberately NOT filtered by status (gh#734 review). Ingestion moves
        // a partially-filled order to Cancelled when its remainder is cancelled or rejected, and the Fill rows for
        // the lots that DID trade still stand: the executions happened and the money is real. Selecting on
        // Filled/PartiallyFilled dropped that entry leg entirely, so the round trip could never balance and was
        // never journalled. What proves an order executed is the presence of FILLS, which the query below reads --
        // venue truth, not the status the order happened to end in.
        List<Order> orders = await database.Orders
            .Where(order => order.AccountId == account.AccountId && order.Instrument == exit.Contract.Key)
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return false; // nothing of ours executed on this contract
        }

        // Bounded at the flat event's own time (gh#734 review). A flat event can be processed LATE -- after a new
        // position has already reopened on the same contract. Without this bound the query returns every fill,
        // including the reopened cycle's, and CurrentCycleStart then slices to that newest cycle: the round trip THIS
        // event closed is skipped and, because the boundary is fills-derived (no persisted watermark), never
        // journalled at all -- gh#731's requirement to preserve the closed trip even when a new position has
        // reopened. Restricting candidates to executions at or before `exit.At` composes the trip the event reports;
        // it also bounds the downstream closing-fill and CurrentCycleStart reads, so all three read the same window.
        List<Guid> orderIds = [.. orders.Select(order => order.Id)];
        List<Fill> fills = await database.Fills
            .Where(fill => orderIds.Contains(fill.OrderId) && fill.ExecutedAt <= exit.At)
            .ToListAsync(cancellationToken);
        if (fills.Count == 0)
        {
            return false;
        }

        Dictionary<Guid, OrderSide> sideByOrder = orders.ToDictionary(order => order.Id, order => order.Side);

        // The current round trip is the fills SINCE the position was last flat -- derived from the fills themselves,
        // not from the last JOURNALLED close.
        //
        // A close-time watermark keyed on a written Trade advances only on SUCCESS, so the fills of a REFUSED flat
        // (a stop-and-reverse, say) would stay in the candidate set forever: every later trip on the contract is
        // recomposed with them, reads as another reversal, and is refused too -- journaling wedged for that
        // account+contract, and DailyRealizedReader and the R-4 throttle starved of the rows gh#731 exists to feed
        // (gh#734 review). The fills carry the boundary themselves: running exposure returns to flat at the end of
        // each completed cycle, journalled or not, so slicing at the last such flat advances past a refused cycle
        // exactly as it does a journalled one -- and past an earlier journalled trip's fills too, so a second round
        // trip on the contract composes from only its own. No persisted watermark, and idempotency still holds: a
        // replay recomposes the same latest cycle and the unique ClosingFillId index rejects the second write.
        // A DETERMINISTIC total order (gh#734 adversarial review). ExecutedAt alone leaves fills that share an
        // instant in the query's arbitrary row order (no ORDER BY above, and Fill carries no venue ordinal), and a
        // stable OrderBy preserves it -- so the boundary walk, the closing fill and the opening fill could each land
        // differently on a replay. Breaking ties by Id (stable per fill across replays) makes every downstream pick
        // reproducible: the same closing-fill natural key each time, so the unique index actually backstops a replay.
        List<Fill> ordered = [.. fills.OrderBy(fill => fill.ExecutedAt).ThenBy(fill => fill.Id)];
        List<Fill> unjournalled = [.. ordered.Skip(CurrentCycleStart(ordered, sideByOrder))];
        if (unjournalled.Count == 0)
        {
            return false;
        }

        // Each candidate carries its Fill.Id: a composed leg's opening and closing fill ids are the trade's natural
        // key (gh#759, ADR-0022).
        RoundTripFill[] candidates =
        [
            .. unjournalled.Select(fill => new RoundTripFill(
                fill.Id, sideByOrder[fill.OrderId], fill.Price, fill.Size, fill.ExecutedAt)
            {
                Fees = fill.Fees,
            }),
        ];

        if (!TradeRoundTrip.TryCompose(candidates, out IReadOnlyList<RoundTrip> trips))
        {
            // Never closed, or genuinely ambiguous (an unclassifiable fill side, an open-from-flat whose direction is
            // not decidable, or a window that does not reconcile to flat). Deliberately silent about writing anything,
            // but loud enough to investigate: a position went flat and produced no journal row.
            _logger.LogInformation(
                "Flat {Contract} on account {Account}: {Fills} unjournalled fill(s) do not form completed round "
                + "trips (still open, or genuinely ambiguous); no Trade written (gh#759 refuse-don't-guess).",
                exit.Contract, exit.Account, unjournalled.Count);
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalNotComposable);
            return false;
        }

        // FIFO composition separates every leg of the window: a scale-in, a partial exit, and a stop-and-reverse each
        // become their own per-leg trip (gh#759, ADR-0022). A STABLE or APPEND-ONLY replay recomposes the SAME
        // (ClosingFillId, OpeningFillId) key per leg, so the per-trip dedup below skips it and re-processing cannot
        // double-count. A RE-PAIRED fill set does not: a late, out-of-order fill (arrival order is arbitrary -- why the
        // fills are ExecutedAt-sorted, not delivery-ordered) landing BEFORE an already-journalled close makes FIFO
        // split the same fills into DIFFERENT legs with different keys, and the exact-pair dedup cannot tell the old
        // row is now orphaned -- writing the new legs would DOUBLE-COUNT the overlap into the daily governor (gh#759
        // review, blocking; gh#734's spans-a-journalled-close guard used to be the net). Fail closed: if any journalled
        // trade references a fill in THIS window but is not one of the legs we are about to write, the pairing changed
        // -- refuse the whole flat rather than double-count. A stable replay never trips this (those rows ARE among
        // `trips`, so `composedKeys` contains them); only a genuine re-pairing does.
        Dictionary<Guid, Fill> fillById = unjournalled.ToDictionary(fill => fill.Id);
        List<Guid?> windowFillIds = [.. fillById.Keys.Select(id => (Guid?)id)];
        HashSet<(Guid? Closing, Guid? Opening)> composedKeys =
            [.. trips.Select(trip => ((Guid?)trip.ClosingFillId, (Guid?)trip.OpeningFillId))];
        HashSet<Guid?> composedClosingFillIds = [.. trips.Select(trip => (Guid?)trip.ClosingFillId)];
        var overlapping = await database.Trades
            .Where(trade => trade.AccountId == account.AccountId && trade.Instrument == exit.Contract.Key
                && (windowFillIds.Contains(trade.ClosingFillId) || windowFillIds.Contains(trade.OpeningFillId)))
            .Select(trade => new { trade.ClosingFillId, trade.OpeningFillId })
            .ToListAsync(cancellationToken);
        // A row is EXPLAINED by this composition -- so NOT a re-pairing -- if its full (Closing, Opening) key is one of
        // the legs we are about to write, OR it is a pre-#759 LEGACY row (null OpeningFillId) whose ClosingFillId
        // matches a composed trip: the old single-key format of the same trip, journalled before this column existed.
        // Without that legacy exception an ordinary replay of ANY pre-migration flat would false-fire this guard and
        // pollute JournalBoundaryMergeRefused (gh#759 review). Pre-migration windows were only ever balanced single
        // trips (scale-in / reverse were refused), so a legacy ClosingFillId belongs to exactly one recomposed leg.
        if (overlapping.Any(row =>
            !composedKeys.Contains((row.ClosingFillId, row.OpeningFillId))
            && !(row.OpeningFillId == null && composedClosingFillIds.Contains(row.ClosingFillId))))
        {
            _logger.LogWarning(
                "Flat {Contract} on account {Account}: a fill in this window is already journalled under a DIFFERENT "
                + "pairing -- the fill set re-paired (a late, out-of-order fill). Refusing rather than double-count into "
                + "the daily governor (gh#759). Investigate the venue's fill delivery / reconcile the affected trades.",
                exit.Contract, exit.Account);
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalBoundaryMergeRefused);
            return false;
        }

        bool anyWritten = false;
        foreach (RoundTrip trip in trips)
        {
            anyWritten |= await JournalTripAsync(database, account, exit, orders, fillById, trip, cancellationToken);
        }

        return anyWritten;
    }

    /// <summary>
    /// Journals one composed leg (gh#759, ADR-0022): idempotent on the <c>(ClosingFillId, OpeningFillId)</c> natural
    /// key, with placement-time truth taken from the leg's <b>opening</b> fill's own order (R-14). One
    /// <c>SaveChanges</c> per leg, so one leg's concurrent-writer race or write fault cannot roll back the others.
    /// </summary>
    /// <returns><see langword="true"/> when this leg wrote a row.</returns>
    private async Task<bool> JournalTripAsync(
        TradingCopilotDbContext database,
        JournalAccount account,
        PositionEvent exit,
        IReadOnlyList<Order> orders,
        IReadOnlyDictionary<Guid, Fill> fillById,
        RoundTrip trip,
        CancellationToken cancellationToken)
    {
        // Idempotent: a replayed flat recomposes the same legs down to the same natural key. The COMPOSITE key is
        // required because one closing fill can retire two legs (a spanning exit), so ClosingFillId alone is no longer
        // unique (ADR-0022). A pre-#759 LEGACY row (null OpeningFillId) is recognized by ClosingFillId ALONE -- its old
        // single-key format is the already-journalled version of this leg, so a replay of a pre-migration flat is the
        // ordinary idempotent skip, not a re-pairing (gh#759 review). Post-migration rows always carry a non-null
        // OpeningFillId, so the null branch matches only genuine legacy rows. The unique index below is the backstop.
        bool alreadyJournalled = await database.Trades.AnyAsync(
            trade => trade.ClosingFillId == trip.ClosingFillId
                && (trade.OpeningFillId == trip.OpeningFillId || trade.OpeningFillId == null),
            cancellationToken);
        if (alreadyJournalled)
        {
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalAlreadyJournalled);
            return false;
        }

        // The entry order is the one that produced THIS leg's opening fill -- the placement whose facts the trade
        // inherits (Mode / SuggestionId / PointValue), never re-picked from `orders` by side (a zero-fill
        // Rejected/Cancelled order on the entry side would blend the wrong Mode into the trip, the R-14 hazard this
        // writer exists to avoid, gh#734 review). On a reversal the opening fill is the reversing fill itself, so the
        // short leg correctly inherits the reversing order's placement.
        Order entryOrder = orders.First(order => order.Id == fillById[trip.OpeningFillId].OrderId);

        decimal pointValue = entryOrder.PointValue;
        if (pointValue <= 0m)
        {
            // Fail closed: without a point value the money is unknowable, and a wrong RealizedPnL feeds the daily
            // governor. Leave this leg unjournalled and say so.
            _logger.LogError(
                "Flat {Contract} on account {Account}: order {Order} snapshotted no point value; the round trip's "
                + "realized P&L cannot be computed and no Trade was written. Investigate.",
                exit.Contract, exit.Account, entryOrder.Id);
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalNoPointValue);
            return false;
        }

        InstrumentSpec spec = InstrumentSpec.Create(
            InstrumentId.Parse(entryOrder.Instrument),
            entryOrder.TickSize > 0m ? entryOrder.TickSize : 0.01m,
            pointValue);

        decimal realized = spec.RealizedPnL(
            new Price(trip.EntryPrice), new Price(trip.ExitPrice), trip.EntrySide, trip.Size);

        Trade record = new()
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            AccountId = account.AccountId,
            SuggestionId = entryOrder.SuggestionId,
            Instrument = entryOrder.Instrument,
            Side = trip.EntrySide,
            Size = trip.Size,
            EntryPrice = trip.EntryPrice,
            ExitPrice = trip.ExitPrice,
            RealizedPnL = realized,
            Mode = entryOrder.Mode, // placement-time truth, never the account's current declaration (R-14)
            ClosedAt = trip.ClosedAt,
            OpeningFillId = trip.OpeningFillId,
            ClosingFillId = trip.ClosingFillId,
        };
        database.Trades.Add(record);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsTradeNaturalKeyViolation(exception))
        {
            // The unique (ClosingFillId, OpeningFillId) index rejected a replay -- idempotent by construction, NARROWED
            // to that one violation (gh#747): any OTHER write fault falls through to the catch below rather than being
            // mistaken for a benign replay. A concurrent writer already journalled this exact leg. Detach the rejected
            // insert so it cannot re-flush on the NEXT leg's SaveChanges in this shared context and roll that leg back
            // too (gh#759 review).
            database.Entry(record).State = EntityState.Detached;
            _logger.LogInformation(
                "Round trip {Opening} -> {Closing} is already journalled for account {Account}; idempotent skip.",
                trip.OpeningFillId, trip.ClosingFillId, exit.Account);
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalDuplicateRejected);
            return false;
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            // NOT the idempotent dupe -- a REAL write fault (a CHECK / FK violation, a serialization failure, a lost
            // connection). Swallowing it would silently drop the Trade and under-report the day's realized P&L into the
            // daily governor with nothing to see (gh#747). Record the distinct outcome and RETHROW -- the host's
            // JournalRoundTripSafelyAsync logs it and the stream continues. The `when (!IsCancellationRequested)` guard
            // excludes a SHUTDOWN cancellation Npgsql wraps as a DbUpdateException (SqlState 57014), keeping the
            // JournalWriteFailed metric honest on a graceful stop; a genuine fault is never masked.
            _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWriteFailed);
            throw;
        }

        _metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWritten);
        _logger.LogInformation(
            "Journalled a {Side} round trip of {Size} on {Contract} for account {Account}: realized {Realized}.",
            trip.EntrySide, trip.Size, exit.Contract, exit.Account, realized);
        return true;
    }

    /// <summary>
    /// The unique index on <c>(ClosingFillId, OpeningFillId)</c> — the round trip leg's natural key (ADR-0022, was
    /// <c>ClosingFillId</c> alone until a spanning exit made that non-unique), and the <b>only</b> constraint whose
    /// violation is the idempotent replay this writer expects (a concurrent writer journalled the same leg first). This
    /// literal must equal the index's real database name (set by EF's default convention in <c>TradingCopilotDbContext</c>
    /// and emitted by the migration); a model-metadata unit test pins it so a rename or an added <c>HasDatabaseName</c>
    /// cannot silently break the match and demote the catch-side backstop (gh#747 review).
    /// </summary>
    internal const string TradeNaturalKeyIndex = "IX_Trades_ClosingFillId_OpeningFillId";

    /// <summary>
    /// Whether a <see cref="DbUpdateException"/> is the idempotent <see cref="TradeNaturalKeyIndex"/> unique-violation
    /// — and <b>only</b> that (gh#747). PostgreSQL surfaces the error as a <see cref="PostgresException"/> inner with
    /// <c>SqlState</c> <see cref="PostgresErrorCodes.UniqueViolation"/> and the offending <c>ConstraintName</c>; a
    /// violation of any OTHER constraint (a CHECK, an FK), or any other write fault, is a <b>real</b> failure this
    /// writer must not swallow as a benign skip — a silently-dropped Trade under-reports the day's realized P&amp;L into
    /// the daily governor. The EF in-memory provider throws a bare <see cref="DbUpdateException"/> with no
    /// <see cref="PostgresException"/> inner, so it never matches here (in-memory idempotency is the pre-check above).
    /// </summary>
    /// <param name="exception">The write fault <c>SaveChanges</c> raised.</param>
    /// <returns><see langword="true"/> only for the trade natural-key unique violation.</returns>
    internal static bool IsTradeNaturalKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TradeNaturalKeyIndex,
        };
    }

    /// <summary>
    /// The index at which the current round trip begins: one past the last fill that returned running exposure to
    /// flat <b>at a clean instant boundary</b>. Buy adds, Sell subtracts; a fill whose side is neither leaves exposure
    /// unchanged and is left for <see cref="TradeRoundTrip"/> to refuse. <paramref name="ordered"/> must be in
    /// execution order.
    /// </summary>
    /// <remarks>
    /// A zero-crossing that lands <b>inside</b> a same-<see cref="Fill.ExecutedAt"/> group is <b>not</b> a cycle
    /// boundary (gh#809). Within one instant there is no venue sequence, so whether running exposure passes through
    /// zero there is decided only by the arbitrary <c>(ExecutedAt, Id)</c> tie-break — slicing on it would guess a
    /// boundary <see cref="Fill.Id"/> alone chose, journalling one side's reading and silently dropping the other
    /// cycle's realized P&amp;L (no watermark re-derives it) into the R-4/R-5 daily governor. Leaving the tie in the
    /// window instead lets the single ambiguity gate in <see cref="TradeRoundTrip.TryCompose"/> refuse a mixed-side
    /// tie (and FIFO compose a genuine same-side reversal). A zero-crossing at a group's <i>clean end</i> — the next
    /// fill is strictly later — is a real boundary: the sum over a same-instant group is order-independent, so it
    /// still advances.
    /// </remarks>
    private static int CurrentCycleStart(
        IReadOnlyList<Fill> ordered, IReadOnlyDictionary<Guid, OrderSide> sideByOrder)
    {
        int running = 0;
        int start = 0;
        for (int index = 0; index < ordered.Count; index++)
        {
            running += sideByOrder[ordered[index].OrderId] switch
            {
                OrderSide.Buy => ordered[index].Size,
                OrderSide.Sell => -ordered[index].Size,
                _ => 0,
            };

            // A return to flat before the final fill closes a prior cycle -- but only at a CLEAN instant boundary. If
            // the next fill shares this instant, the flat is an artifact of intra-instant ordering (gh#809): do not
            // slice, so the tie stays in the window for TradeRoundTrip.TryCompose's gate to judge.
            if (running == 0
                && index < ordered.Count - 1
                && ordered[index].ExecutedAt != ordered[index + 1].ExecutedAt)
            {
                start = index + 1;
            }
        }

        return start;
    }

    private async Task<JournalAccount?> ResolveAccountAsync(
        VenueAccountId account, CancellationToken cancellationToken)
    {
        var match = await _discovery.Accounts
            .IgnoreQueryFilters()
            .Join(
                _discovery.Connections.IgnoreQueryFilters(),
                owned => owned.ConnectionId,
                connection => connection.Id,
                (owned, connection) => new { Account = owned, connection.CredentialKey })
            .Where(pair => pair.Account.VenueAccountKey == account.Key
                && pair.CredentialKey == _projectX.CredentialKey)
            .Select(pair => new { pair.Account.Id, pair.Account.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        return match is null ? null : new JournalAccount(match.UserId, match.Id);
    }

    /// <summary>The owner and our account id — the R-20 identity the journal write is scoped to.</summary>
    private sealed record JournalAccount(Guid UserId, Guid AccountId);

    /// <summary>The owning operator, so the per-owner context never touches another's rows.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
