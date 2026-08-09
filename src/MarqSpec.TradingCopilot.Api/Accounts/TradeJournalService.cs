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

        // The orders of ours on the contract that actually executed -- the journal's own record of the round trip.
        List<Order> orders = await database.Orders
            .Where(order => order.AccountId == account.AccountId
                && order.Instrument == exit.Contract.Key
                && (order.Status == OrderStatus.Filled || order.Status == OrderStatus.PartiallyFilled))
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

        // Already journalled? Every fill of a round trip we have written is spoken for by its Trade's closing
        // fill; re-composing over them would form a second, duplicate trade from the same executions.
        HashSet<Guid> journalledClosings = [.. await database.Trades
            .Where(trade => trade.AccountId == account.AccountId && trade.ClosingFillId != null)
            .Select(trade => trade.ClosingFillId!.Value)
            .ToListAsync(cancellationToken)];

        Dictionary<Guid, OrderSide> sideByOrder = orders.ToDictionary(order => order.Id, order => order.Side);
        List<Fill> unjournalled = [.. fills.Where(fill => !journalledClosings.Contains(fill.Id))];
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

        // Point value is the one snapshotted on the ENTRY order -- a fact of placement, not of now.
        Order entryOrder = orders
            .Where(order => order.Side == roundTrip.EntrySide)
            .OrderBy(order => order.PlacedAt)
            .First();

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
