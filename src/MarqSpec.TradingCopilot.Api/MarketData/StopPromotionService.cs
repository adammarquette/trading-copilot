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
    private readonly ILogger<StopPromotionService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="logger">The logger.</param>
    public StopPromotionService(TradingCopilotDbContext database, ILogger<StopPromotionService> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Promotes every hidden stop on <paramref name="contractKey"/> that this quote brings within its band.
    /// </summary>
    /// <param name="venue">The venue the quote came from (its id tags the placed stop).</param>
    /// <param name="contractKey">The contract the quote is for (e.g. <c>CON.F.US.MES.U26</c>).</param>
    /// <param name="bid">The best bid — the exit price for a long, so a long's promotion is measured on it.</param>
    /// <param name="ask">The best ask — the exit price for a short.</param>
    /// <param name="executor">The venue executor the native stop is transmitted through.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were promoted.</returns>
    public async Task<int> PromoteForQuoteAsync(
        VenueId venue,
        string contractKey,
        decimal bid,
        decimal ask,
        IOrderExecutor executor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);

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

            // The side that would hit the protective stop: a long exits at the bid, a short at the ask.
            decimal price = plan.Side == OrderSide.Buy ? bid : ask;
            if (!plan.ShouldPromote(new Price(price)))
            {
                continue;
            }

            // Transmit FIRST, then record native. If the venue rejects, the exception propagates and the plan
            // stays Hidden -- marking it Native without an exchange-held stop would be a lie on a safety path.
            OrderRequest stop = new(
                VenueAccountId.Create(venue, account.VenueAccountKey),
                VenueContractId.Create(venue, contractKey),
                Opposite(plan.Side),
                OrderType.Stop,
                order.Size,
                LimitPrice: null,
                StopPrice: plan.ActualStop);

            await executor.PlaceOrderAsync(stop, cancellationToken);

            record.Staging = StopStaging.Native;
            promoted++;

            _logger.LogInformation(
                "Promoted the actual stop for order {OrderId} ({Contract}) to native at {Stop}.",
                order.Id, contractKey, plan.ActualStop);
        }

        if (promoted > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        return promoted;
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
