using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Reads the operator's journaled fills on one account + instrument over a time window (gh#792) — the read behind
/// the gh#727 chart fill-marker overlay. A <b>journal</b> read (the <see cref="Fill"/> table joined to its
/// <see cref="Order"/>), <b>not</b> venue truth: the fill-level companion to the venue-truth orders / positions
/// reads, but sourced from the durable journal a fill is written to as it happens (gh#731).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FillReconciliationService"/>, which asks the <i>venue</i> whether a tagged order ever
/// filled (gh#631) — a different question, a different source, and a veto rather than a list.
/// </para>
/// <para>
/// A <see cref="Fill"/> carries only venue facts (price / size / fees / time); the instrument, account and side
/// live on the joined <see cref="Order"/>. Both are <see cref="Data.Tenancy.IUserOwned"/>, so the request-scoped
/// context's global filter scopes <b>both</b> sides of the join to the operator with no explicit predicate (R-20).
/// </para>
/// <para>
/// Window-bounded like the bars read (gh#644): a window returning more than <see cref="MaxFills"/> rows is refused
/// (the endpoint maps it to a 400), never silently truncated — a partial overlay would misrepresent the record.
/// </para>
/// </remarks>
public sealed class FillJournalReadService
{
    /// <summary>The most fills a single window may return before it is refused as too wide (the gh#644 discipline).</summary>
    internal const int MaxFills = 1000;

    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The request-scoped context (carries the R-20 filter on both Fill and Order).</param>
    public FillJournalReadService(TradingCopilotDbContext database) => _database = database;

    /// <summary>
    /// Reads the operator's fills on <paramref name="accountId"/> for <paramref name="instrument"/> in
    /// <c>[from, to)</c>, ascending by execution time.
    /// </summary>
    /// <param name="accountId">The account (R-20-scoped to the caller).</param>
    /// <param name="instrument">
    /// The instrument whose fills to return — matched (case-insensitively) against the order's <b>venue-neutral
    /// symbol</b> (<see cref="Order.Symbol"/>, e.g. <c>ES</c>), the same symbol the chart charts. An order with no
    /// recorded symbol cannot be attributed to an instrument and is excluded.
    /// </param>
    /// <param name="from">Window start, inclusive.</param>
    /// <param name="to">Window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The read, or <see langword="null"/> when the account is not found / not owned by the caller (the endpoint
    /// maps that to 404, so existence is never disclosed). <see cref="FillJournalRead.WindowTooWide"/> is set when
    /// the window exceeds <see cref="MaxFills"/> — the endpoint maps that to a 400.
    /// </returns>
    public async Task<FillJournalRead?> ReadAsync(
        Guid accountId,
        InstrumentId instrument,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        Account? account = await _database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null; // not found / not owned (R-20) -> 404
        }

        string wanted = instrument.Symbol; // InstrumentId normalizes to trimmed upper-case.

        // The fills on THIS account + instrument in the window. A Fill has no instrument / account / side, so join to
        // the Order for them. Match the venue-neutral Symbol (what the chart charts), not the resolved contract, whose
        // key rolls across expiries. Fetch one past the cap to detect an over-wide window (refuse, never truncate).
        List<ExecutedFill> fills = await _database.Fills.AsNoTracking()
            .Where(fill => fill.ExecutedAt >= from && fill.ExecutedAt < to)
            .Join(
                _database.Orders,
                fill => fill.OrderId,
                order => order.Id,
                (fill, order) => new { fill, order })
            .Where(pair =>
                pair.order.AccountId == accountId
                && pair.order.Symbol != null
                && pair.order.Symbol.ToUpper() == wanted)
            .OrderBy(pair => pair.fill.ExecutedAt)
            .Take(MaxFills + 1)
            .Select(pair => new ExecutedFill(
                pair.fill.VenueFillKey,
                pair.order.Side.ToString(),
                pair.fill.Price,
                pair.fill.Size,
                pair.fill.Fees,
                pair.fill.ExecutedAt))
            .ToListAsync(cancellationToken);

        return fills.Count > MaxFills
            ? new FillJournalRead(WindowTooWide: true, [])
            : new FillJournalRead(WindowTooWide: false, fills);
    }
}

/// <summary>
/// One journaled fill for the chart's fill-marker overlay (gh#727 / gh#792): the execution's price, size, time and
/// fees, plus the side of the order it executed. Prices are the journal's <c>decimal</c>s — fine to draw, never to
/// size an order against.
/// </summary>
/// <param name="VenueFillKey">The venue's own fill handle, when it reported one (a dedupe key).</param>
/// <param name="Side">The side of the order this fill executed (<c>Buy</c> / <c>Sell</c>).</param>
/// <param name="Price">The execution price.</param>
/// <param name="Size">The filled size in contracts.</param>
/// <param name="Fees">Fees / commissions attributed to the fill.</param>
/// <param name="ExecutedAt">When the fill executed.</param>
public sealed record ExecutedFill(
    string? VenueFillKey,
    string Side,
    decimal Price,
    int Size,
    decimal Fees,
    DateTimeOffset ExecutedAt);

/// <summary>
/// The result of a fills journal read (gh#792). When <see cref="WindowTooWide"/>, <see cref="Fills"/> is empty and
/// the endpoint refuses with a 400 rather than return a truncated overlay.
/// </summary>
/// <param name="WindowTooWide">Whether the window exceeded the read cap (a refusal, not a truncation).</param>
/// <param name="Fills">The fills in the window, ascending by execution time (empty when the window is too wide).</param>
public sealed record FillJournalRead(bool WindowTooWide, IReadOnlyList<ExecutedFill> Fills);
