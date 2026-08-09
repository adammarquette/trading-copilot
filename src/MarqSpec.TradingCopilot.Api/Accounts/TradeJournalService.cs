using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// <b>Idempotent by construction.</b> The closing fill is the trade's natural key, behind a unique index — a
/// replayed flat event recomposes the same round trip and the second insert is rejected, exactly as the fill
/// consumer dedupes on <c>{ OrderId, VenueFillKey }</c>. This matters more than tidiness: a double-written trade
/// would double-count the day's realized P&amp;L into the daily governor and mis-state the operator's headroom.
/// </para>
/// <para>
/// <b>Foundational scope.</b> Only the balanced single <b>enter → exit → flat</b> round trip is journalled;
/// scale-in with a partial exit and stop-and-reverse are refused by <see cref="TradeRoundTrip"/> and left for a
/// documented follow-up. Refusing writes nothing, which is the honest outcome — a wrong <c>RealizedPnL</c> feeding
/// the governor is worse than a missing one, which is visibly zero.
/// </para>
/// </remarks>
public sealed class TradeJournalService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<TradeJournalService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to resolve the owning account (across owners).</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public TradeJournalService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<TradeJournalService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _discovery = discovery;
        _options = options;
        _projectX = projectXOptions.Value;
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

        List<Guid> orderIds = [.. orders.Select(order => order.Id)];
        List<Fill> fills = await database.Fills
            .Where(fill => orderIds.Contains(fill.OrderId))
            .ToListAsync(cancellationToken);
        if (fills.Count == 0)
        {
            return false;
        }

        // Already journalled? The boundary is TIME, not the closing fill id (gh#734 review).
        //
        // A written Trade records only its CLOSING fill, so excluding fills by `ClosingFillId` removes exactly ONE
        // fill per journalled trade and leaves every earlier ENTRY fill in the candidate set indefinitely. The
        // second round trip on a contract was then composed from {old entry, new entry, new exit} -- two entries
        // against one exit, which cannot balance -- so it was refused and logged as a scale-in, and no trade after
        // the first was ever written for that account+contract. For a day-trading account that is the common case,
        // not an edge one, and it puts DailyRealizedReader and the R-4 throttle back to reading ~zero, which is the
        // gap gh#731 exists to close.
        //
        // Round trips on one contract are sequential and non-overlapping -- the account is flat between them, which
        // is what triggers this pass at all -- so the latest journalled close is a sound watermark. Strictly `>`:
        // the closing fill of the previous trip sits exactly ON the boundary and is already spoken for.
        DateTimeOffset? lastJournalledClose = await database.Trades
            .Where(trade => trade.AccountId == account.AccountId
                && trade.Instrument == exit.Contract.Key
                && trade.ClosedAt != null)
            .MaxAsync(trade => (DateTimeOffset?)trade.ClosedAt, cancellationToken);

        Dictionary<Guid, OrderSide> sideByOrder = orders.ToDictionary(order => order.Id, order => order.Side);
        List<Fill> unjournalled = lastJournalledClose is null
            ? fills
            : [.. fills.Where(fill => fill.ExecutedAt > lastJournalledClose.Value)];
        if (unjournalled.Count == 0)
        {
            return false;
        }

        RoundTripFill[] candidates =
        [
            .. unjournalled.Select(fill => new RoundTripFill(
                sideByOrder[fill.OrderId], fill.Price, fill.Size, fill.ExecutedAt)
            {
                Fees = fill.Fees,
            }),
        ];

        if (!TradeRoundTrip.TryCompose(candidates, out RoundTrip? roundTrip))
        {
            // Unbalanced (scale-in / partial exit / reversal) or never closed. Deliberately silent about writing
            // anything, but loud enough to investigate: a position went flat and produced no journal row.
            _logger.LogInformation(
                "Flat {Contract} on account {Account}: {Fills} unjournalled fill(s) do not form a single balanced "
                + "round trip (scale-in, partial exit, or reversal); no Trade written (gh#731 defers those).",
                exit.Contract, exit.Account, unjournalled.Count);
            return false;
        }

        // The closing fill: the latest execution on the exit leg -- the natural key this write dedupes on.
        OrderSide exitSide = roundTrip!.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        Fill closingFill = unjournalled
            .Where(fill => sideByOrder[fill.OrderId] == exitSide)
            .OrderByDescending(fill => fill.ExecutedAt)
            .First();

        // The entry order is the one that actually produced this round trip's OPENING fill -- taken from the fills
        // that compose the trip, never re-picked from `orders` by side. `orders` is deliberately unfiltered by
        // status for venue truth (above), so it also holds zero-fill Rejected/Cancelled orders on the entry side --
        // a risk-gate rejection, or a resting order cancelled before any execution. Selecting the earliest of THOSE
        // by PlacedAt could land on an order that never traded, and the trade would inherit its Mode / SuggestionId /
        // PointValue: a Live round trip journalled under an earlier rejected order's Practice is exactly the R-14
        // blending this writer copies placement-time Mode to avoid (gh#734 review). The opening fill's own order is
        // the placement whose facts this trade inherits; a zero-fill order has no fill here and cannot be it.
        Fill openingFill = unjournalled
            .Where(fill => sideByOrder[fill.OrderId] == roundTrip.EntrySide)
            .OrderBy(fill => fill.ExecutedAt)
            .First();
        Order entryOrder = orders.First(order => order.Id == openingFill.OrderId);

        decimal pointValue = entryOrder.PointValue;
        if (pointValue <= 0m)
        {
            // Fail closed: without a point value the money is unknowable, and a wrong RealizedPnL feeds the daily
            // governor. Leave the round trip unjournalled and say so.
            _logger.LogError(
                "Flat {Contract} on account {Account}: order {Order} snapshotted no point value; the round trip's "
                + "realized P&L cannot be computed and no Trade was written. Investigate.",
                exit.Contract, exit.Account, entryOrder.Id);
            return false;
        }

        InstrumentSpec spec = InstrumentSpec.Create(
            InstrumentId.Parse(entryOrder.Instrument),
            entryOrder.TickSize > 0m ? entryOrder.TickSize : 0.01m,
            pointValue);

        decimal realized = spec.RealizedPnL(
            new Price(roundTrip.EntryPrice), new Price(roundTrip.ExitPrice), roundTrip.EntrySide, roundTrip.Size);

        database.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            AccountId = account.AccountId,
            SuggestionId = entryOrder.SuggestionId,
            Instrument = entryOrder.Instrument,
            Side = roundTrip.EntrySide,
            Size = roundTrip.Size,
            EntryPrice = roundTrip.EntryPrice,
            ExitPrice = roundTrip.ExitPrice,
            RealizedPnL = realized,
            Mode = entryOrder.Mode, // placement-time truth, never the account's current declaration (R-14)
            ClosedAt = roundTrip.ClosedAt,
            ClosingFillId = closingFill.Id,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique ClosingFillId index rejected a replay -- idempotent by construction, not by an inspecting
            // branch. A concurrent writer already journalled this exact round trip.
            _logger.LogInformation(
                "Round trip closing on fill {Fill} is already journalled for account {Account}; idempotent skip.",
                closingFill.Id, exit.Account);
            return false;
        }

        _logger.LogInformation(
            "Journalled a {Side} round trip of {Size} on {Contract} for account {Account}: realized {Realized}.",
            roundTrip.EntrySide, roundTrip.Size, exit.Contract, exit.Account, realized);
        return true;
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
