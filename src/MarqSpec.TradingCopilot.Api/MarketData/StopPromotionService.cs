using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The stop-promotion watcher's core (ADR-0007, gh#153): on each quote, promote any <b>hidden</b> actual stop
/// whose price has come within its band — transmit it as a native working order and record it native. The
/// safety stop is already native (inc 3); this promotes the <i>working</i> stop from synthetic to exchange-held
/// as price approaches, so a fill is caught natively rather than depending on the platform staying live.
/// </summary>
/// <remarks>
/// This runs as background plumbing with <b>no authenticated user</b>, so its queries deliberately
/// <c>IgnoreQueryFilters</c> the R-20 default-deny filter —
/// the watcher acts for the deployment, promoting stops for whoever owns them (on a single-operator deployment,
/// the operator; a read-only mentee never owns one). Ownership is preserved on write; nothing is re-assigned.
/// </remarks>
public sealed class StopPromotionService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IIndicatorSource _indicators;
    private readonly IndicatorOptions _indicatorOptions;
    private readonly ILogger<StopPromotionService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="indicators">
    /// The indicator read seam (gh#310) — consulted only for an ATR band, and the reason <c>StopPlan</c> never
    /// has to reach for a measurement itself.
    /// </param>
    /// <param name="indicatorOptions">Identifies <i>which</i> ATR a band means: the period and the bar size.</param>
    /// <param name="logger">The logger.</param>
    public StopPromotionService(
        TradingCopilotDbContext database,
        IIndicatorSource indicators,
        IOptions<IndicatorOptions> indicatorOptions,
        ILogger<StopPromotionService> logger)
    {
        ArgumentNullException.ThrowIfNull(indicatorOptions);

        _database = database;
        _indicators = indicators;
        _indicatorOptions = indicatorOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Promotes every hidden stop on <paramref name="contractKey"/> that this quote brings within its band.
    /// </summary>
    /// <param name="venueId">The venue the quote came from — its id tags the placed stop and the accounts read.</param>
    /// <param name="contractKey">The contract the quote is for (e.g. <c>CON.F.US.MES.U26</c>).</param>
    /// <param name="bid">The best bid — the exit price for a long, so a long's promotion is measured on it.</param>
    /// <param name="ask">The best ask — the exit price for a short.</param>
    /// <param name="venue">
    /// The trading venue — used both to <b>confirm the position is still open</b> before promoting and to transmit
    /// the promoted native stop.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were promoted.</returns>
    public async Task<int> PromoteForQuoteAsync(
        VenueId venueId,
        string contractKey,
        decimal bid,
        decimal ask,
        ITradingVenue venue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        // Background plumbing: no user context, so the R-20 filter is bypassed deliberately (see the class note).
        // Loaded in steps rather than one join -- the intent reads plainly, and it sidesteps join-composition
        // quirks across providers.
        List<StopPlanRecord> hidden = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Hidden)
            .ToListAsync(cancellationToken);
        if (hidden.Count == 0)
        {
            return 0;
        }

        List<Guid> orderIds = [.. hidden.Select(plan => plan.OrderId)];
        Dictionary<Guid, Order> orders = await _database.Orders
            .IgnoreQueryFilters()
            .Where(order => orderIds.Contains(order.Id) && order.Instrument == contractKey)
            .ToDictionaryAsync(order => order.Id, cancellationToken);

        List<Guid> accountIds = [.. orders.Values.Select(order => order.AccountId).Distinct()];
        Dictionary<Guid, Account> accounts = await _database.Accounts
            .IgnoreQueryFilters()
            .Where(account => accountIds.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, cancellationToken);

        // Venue positions are read at most once per account per pass and reused across that account's stops (a
        // null entry records a fetch that failed, so we do not re-hit an unreachable venue for every stop).
        Dictionary<string, IReadOnlyList<PositionSnapshot>?> positionsByAccount = [];

        // Indicator values are read at most once per instrument per pass, and only for a plan that actually needs
        // one. One "as of" for the whole pass, so two plans on the same series can never resolve their bands from
        // different moments.
        Dictionary<string, decimal?> atrByInstrument = [];
        DateTimeOffset asOf = DateTimeOffset.UtcNow;

        int promoted = 0;
        foreach (StopPlanRecord record in hidden)
        {
            // Only stops whose order is on THIS contract (others were filtered out of the orders map).
            if (!orders.TryGetValue(record.OrderId, out Order? order)
                || !accounts.TryGetValue(order.AccountId, out Account? account))
            {
                continue;
            }

            StopPlan plan = record.ToStopPlan(order);

            // Resolve the band HERE, in the caller, and hand StopPlan an absolute distance (gh#311). An ATR band
            // needs a measurement; a pure value object reconstructed from the database is the wrong place to
            // reach for one, and a distance fixed at construction would silently go stale as ATR moved.
            decimal? band = await ResolveBandAsync(plan, atrByInstrument, asOf, order.Id, cancellationToken);
            if (band is null)
            {
                // Unmeasurable -> DO NOT PROMOTE. Substituting a default distance would place the stop at a
                // distance nobody chose; the always-native safety stop remains the floor and the next quote
                // retries once the projection catches up. (Logged once per instrument per pass, inside.)
                continue;
            }

            // The side that would hit the protective stop: a long exits at the bid, a short at the ask.
            decimal price = plan.Side == OrderSide.Buy ? bid : ask;
            if (!plan.ShouldPromote(new Price(price), band.Value))
            {
                continue;
            }

            VenueAccountId venueAccount = VenueAccountId.Create(venueId, account.VenueAccountKey);

            // Position-awareness (gh#183 follow-up, review Finding 4). Promote ONLY for a position the venue still
            // reports open on the entered side. A promotion in flight when a position is flattened -- a manual
            // flatten, the actual stop firing, auto-flatten, the kill switch -- would otherwise place a native
            // protective stop for an already-flat position: a live resting order at the exchange with no position
            // behind it, which the next fill turns into an unasked-for position (the exact hazard gh#183 removes).
            // Flat, reversed, or unconfirmable all fail closed to NOT promoting -- the always-native safety stop
            // remains the floor and OcoExitService retires the plan terminally on the exit event. Uncertainty
            // resolves to the safe state (engineering §9).
            int? openQuantity = await OpenQuantityAsync(
                venue, venueAccount, contractKey, plan.Side, positionsByAccount, cancellationToken);
            if (openQuantity is null)
            {
                continue;
            }

            // Size the promoted stop to what the venue ACTUALLY reports open, capped at this plan's order size
            // (gh#277). A partial scale-out (net < order.Size) must NOT promote a full-size stop -- firing it would
            // close more than is held and reverse the position into an unwanted one. A larger net (other entries on
            // the same contract) is not this plan's to protect, so cap at order.Size. The position was confirmed
            // non-zero and same-side above, so this is always >= 1.
            int protectQuantity = Math.Min(order.Size, openQuantity.Value);

            // Transmit FIRST, then record native. If the venue rejects, the exception propagates and the plan
            // stays Hidden -- marking it Native without an exchange-held stop would be a lie on a safety path.
            OrderRequest stop = new(
                venueAccount,
                VenueContractId.Create(venueId, contractKey),
                Opposite(plan.Side),
                OrderType.Stop,
                protectQuantity,
                LimitPrice: null,
                StopPrice: plan.ActualStop);

            PlacedOrder placed = await venue.PlaceOrderAsync(stop, cancellationToken);

            // Re-read the plan's staging from the DB before recording native (gh#183 follow-up, review Finding 4).
            // Between the position-check above and this point a concurrent actor may have moved the plan off Hidden
            // -- OCO-cancel-on-exit retiring it as the position went flat, or the orphan guard on a connection drop.
            // If it did, the native stop we just transmitted is resting for a position that no longer exists, so we
            // CANCEL IT OURSELVES rather than leave it for another sweep to miss. Each plan is reconciled
            // independently (its own reload + save), so one plan's lost race never affects another's promotion.
            //
            // This is a re-read, NOT an atomic compare-and-swap: a retire that commits in the narrow window between
            // this reload and the save below is still overwritten to a stale Native. That residue is benign -- the
            // watcher never re-acts on a Native plan, and OCO-exit's leg-sweep (which runs after its own retire
            // commit, so after this leg is resting) still cancels the physical leg -- so it is a rare journal/audit
            // blemish, not a live dangling stop. A true CAS would need either a concurrency token (rejected: it is
            // symmetric across every StopPlans writer, so it would turn a lost race in OCO-exit's OWN retire into an
            // unhandled fault that skips its native-leg cleanup -- the very hazard this exists to prevent) or a
            // provider that supports ExecuteUpdate (the in-memory unit-test provider does not).
            await _database.Entry(record).ReloadAsync(cancellationToken);
            if (record.Staging == StopStaging.Hidden)
            {
                record.Staging = StopStaging.Native;
                await _database.SaveChangesAsync(cancellationToken);
                promoted++;

                _logger.LogInformation(
                    "Promoted the actual stop for order {OrderId} ({Contract}) to native at {Stop}.",
                    order.Id, contractKey, plan.ActualStop);
            }
            else
            {
                await CancelDanglingLegAsync(venue, venueAccount, placed, order.Id, contractKey, record.Staging, cancellationToken);
            }
        }

        return promoted;
    }

    /// <summary>
    /// Cancels a native stop that was transmitted for a plan which turned out to be no longer <see cref="StopStaging.Hidden"/>
    /// (retired or orphaned concurrently) — the position it would protect is gone, so the just-placed stop is a
    /// dangling resting order. The service that placed it owns cancelling it; a failure here is the
    /// <c>synthetic_risk</c> case (a live resting order with no position behind it) and is logged at error, never
    /// thrown — the pass must continue for the other stops.
    /// </summary>
    private async Task CancelDanglingLegAsync(
        ITradingVenue venue,
        VenueAccountId account,
        PlacedOrder placed,
        Guid orderId,
        string contractKey,
        StopStaging observed,
        CancellationToken cancellationToken)
    {
        try
        {
            await venue.CancelOrderAsync(account, placed.VenueOrderId, cancellationToken);
            _logger.LogWarning(
                "Promotion for order {OrderId} ({Contract}) lost a race with a concurrent {Staging}; the plan is no "
                + "longer Hidden, so the promotion is discarded and the just-placed native stop {Leg} was cancelled.",
                orderId, contractKey, observed, placed.VenueOrderId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Could not cancel the dangling leg: a live resting native stop now has no position behind it (synthetic_risk).
            // Surface it loudly for manual intervention rather than swallowing -- do NOT rethrow (the other stops must
            // still be processed).
            _logger.LogError(
                error,
                "Promotion for order {OrderId} ({Contract}) lost a race with a concurrent {Staging} AND could not "
                + "cancel the just-placed native stop {Leg} (synthetic_risk): a live resting order may have no "
                + "position behind it. Cancel it manually.",
                orderId, contractKey, observed, placed.VenueOrderId);
        }
    }

    /// <summary>
    /// This plan's promotion band as an absolute price distance — or <see langword="null"/> when it cannot be
    /// measured, which the caller must treat as <b>do not promote</b>.
    /// </summary>
    /// <remarks>
    /// Only an ATR band consults the indicator store, so a tick or distance-fraction plan costs no query and is
    /// unaffected by an indicator outage. The read is cached per instrument for the pass: the watcher runs on
    /// every quote, so a read per plan would put N queries on a hot path and repeat the missing-value warning
    /// once per plan instead of once per pass.
    /// </remarks>
    private async Task<decimal?> ResolveBandAsync(
        StopPlan plan,
        Dictionary<string, decimal?> cache,
        DateTimeOffset asOf,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        decimal? atr = null;
        if (plan.Proximity.Metric == StopProximityMetric.AverageTrueRange)
        {
            string symbol = plan.Instrument.Id.ToString();
            if (!cache.TryGetValue(symbol, out atr))
            {
                atr = await _indicators.GetAverageTrueRangeAsync(
                    plan.Instrument.Id, _indicatorOptions.BandResolutionMinutes, asOf, cancellationToken);
                cache[symbol] = atr;

                if (atr is null or <= 0m)
                {
                    // Named loudly because the failure is otherwise INVISIBLE: the stop simply never promotes,
                    // and "no promotion" looks identical to "price never came close". Causes worth checking are
                    // insufficient bar history, a stalled projection, and a band resolution the backfill is not
                    // archiving (Indicators:BandResolutionMinutes vs Backfill:ResolutionMinutes).
                    _logger.LogWarning(
                        "No ATR({Period}) on {Instrument} at {Resolution}m as of {AsOf}; ATR-banded stops are NOT "
                        + "being promoted. The native safety stop remains the floor until a value exists.",
                        _indicatorOptions.AtrPeriod, symbol, _indicatorOptions.BandResolutionMinutes, asOf);
                }
            }
        }

        decimal? band = plan.Proximity.ResolveDistance(plan.Instrument, plan.Entry, plan.ActualStop, atr);
        if (band is null)
        {
            _logger.LogDebug(
                "Stop plan for order {OrderId} has an unresolvable {Metric} band this pass; not promoting.",
                orderId, plan.Proximity.Metric);
        }

        return band;
    }

    /// <summary>
    /// The venue's live open quantity on <paramref name="contractKey"/> for <paramref name="account"/>, on the same
    /// <paramref name="side"/> the order entered — or <see langword="null"/> when the position is flat, reversed, or
    /// the venue could not be reached (all fail closed, so a promotion never rests on an unverified assumption). The
    /// magnitude lets the caller size the promoted stop to what is <b>actually</b> open rather than the original order
    /// size (gh#277). Reads are cached per account for the pass.
    /// </summary>
    private async Task<int?> OpenQuantityAsync(
        ITradingVenue venue,
        VenueAccountId account,
        string contractKey,
        OrderSide side,
        Dictionary<string, IReadOnlyList<PositionSnapshot>?> cache,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(account.Key, out IReadOnlyList<PositionSnapshot>? positions))
        {
            try
            {
                positions = await venue.GetPositionsAsync(account, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // Cannot confirm the position -- do NOT promote on an unverified assumption. The native safety stop
                // remains the floor and the next quote retries. Cache the failure so one unreachable venue is not
                // re-hit for every stop on this account in the same pass.
                _logger.LogWarning(
                    error,
                    "Stop promotion could not read positions for account {Account}; skipping promotion this pass.",
                    account);
                positions = null;
            }

            cache[account.Key] = positions;
        }

        if (positions is null)
        {
            return null; // unverifiable -> fail closed
        }

        PositionSnapshot? position = positions
            .FirstOrDefault(candidate => string.Equals(candidate.Contract.Key, contractKey, StringComparison.Ordinal));

        // Open only if the venue reports net exposure on the SAME side the order entered -- flat (zero) or reversed
        // (opposite sign) means the protected position is gone, so there is nothing to promote a stop for. Return the
        // live remaining magnitude so the caller can size the promoted stop to what is actually open (gh#277).
        int expectedSign = side == OrderSide.Buy ? 1 : -1;
        return position is not null && position.NetQuantity != 0 && Math.Sign(position.NetQuantity) == expectedSign
            ? Math.Abs(position.NetQuantity)
            : null;
    }

    /// <summary>The protective exit side for a position: a long is protected by a sell, a short by a buy.</summary>
    private static OrderSide Opposite(OrderSide side) => side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

    /// <summary>
    /// Decodes a <c>market.quote</c> event (the shape <c>QuoteIngestionService</c> writes) into the venue,
    /// contract key, and bid/ask the watcher needs. Returns false for any other event type or a malformed
    /// payload — a consumer sees every event on the log, and one it cannot read must be skipped, not fatal.
    /// </summary>
    /// <param name="quoteEvent">The event envelope.</param>
    /// <param name="quote">The decoded quote, when the event is a well-formed quote.</param>
    /// <returns>Whether the event was a decodable quote.</returns>
    public static bool TryDecodeQuote(EventEnvelope quoteEvent, out DecodedQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quoteEvent);
        quote = default;

        if (!string.Equals(quoteEvent.Type, QuoteIngestionService.QuoteEventType, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(quoteEvent.Payload);
            JsonElement root = document.RootElement;

            // The contract is a venue-qualified id (venue:key); the venue also rides the envelope Source.
            string contract = root.GetProperty("contract").GetString() ?? string.Empty;
            string[] parts = contract.Split(':', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            quote = new DecodedQuote(
                VenueId.Parse(parts[0]),
                parts[1],
                root.GetProperty("bid").GetDecimal(),
                root.GetProperty("ask").GetDecimal());
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>A decoded quote: the venue, its contract key, and the best bid/ask.</summary>
    /// <param name="Venue">The venue that produced the quote.</param>
    /// <param name="ContractKey">The contract key (e.g. <c>CON.F.US.MES.U26</c>).</param>
    /// <param name="Bid">The best bid.</param>
    /// <param name="Ask">The best ask.</param>
    public readonly record struct DecodedQuote(VenueId Venue, string ContractKey, decimal Bid, decimal Ask);
}
