using MarqSpec.TradingCopilot.Api.Orders;
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

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The conditional-order firing watcher's core (ADR-0007, gh#198): on each quote, fire every <b>pending</b>
/// conditional whose trigger the quote crossed, and cancel/expire the stale ones. Firing is a <b>new entry</b>,
/// so it runs the <b>authoritative fire-time re-gate</b> (R-12 / R-5 / R-16) through the same
/// <see cref="OrderExecutionService"/> the operator path uses — not a bare transmit like the stop-promotion
/// watcher.
/// </summary>
/// <remarks>
/// <para>
/// Background plumbing with no request user: it <b>discovers</b> pending conditionals across owners with
/// <c>IgnoreQueryFilters</c> (the stop-promotion pattern), then does the work for each owner in a DbContext
/// <b>scoped to that owner</b> — so the reused <see cref="OrderEndpoints.ComposeAsync"/> ladder (declared risk,
/// credential-key guard, flat-account rule, fresh venue truth) stays R-20-correct without duplicating a single
/// safety guard (the gh#148 drift lesson). On a single-operator deployment there is exactly one owner.
/// </para>
/// <para>
/// The fire-time gate is authoritative: a triggered order the gate <i>refuses</i> stays <see cref="ConditionalStatus.Pending"/>
/// and re-decides on the next quote, while a placed one journals its order + stop plan (so the stop-promotion
/// watcher protects it) and records <see cref="ConditionalStatus.Fired"/>. Firing is idempotent — a resolved
/// order never re-fires (<see cref="ConditionalOrder.ShouldFire"/> is Pending-only).
/// </para>
/// </remarks>
public sealed class ConditionalFiringService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly IOptions<ExecutionOptions> _executionOptions;
    private readonly HostTradingEnvironment _environment;
    private readonly ILogger<ConditionalFiringService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover which owners have pending conditionals.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="venueFactory">The venue factory.</param>
    /// <param name="projectXOptions">The process's ProjectX credential-key configuration.</param>
    /// <param name="executionOptions">The R-16 sanity caps and stop-promotion band.</param>
    /// <param name="environment">The R-14 deployment environment.</param>
    /// <param name="logger">The logger.</param>
    public ConditionalFiringService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        ILogger<ConditionalFiringService> logger)
    {
        _discovery = discovery;
        _options = options;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _executionOptions = executionOptions;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Fires / cancels / expires every pending conditional on <paramref name="contractKey"/> this quote resolves.</summary>
    /// <param name="contractKey">The contract the quote is for (e.g. <c>CON.F.US.MES.U26</c>).</param>
    /// <param name="bid">The best bid — a short entry transacts here.</param>
    /// <param name="ask">The best ask — a long entry transacts here.</param>
    /// <param name="now">The current time, supplied by the caller — the domain never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many conditionals were acted on (fired, cancelled, or expired).</returns>
    public async Task<int> ProcessQuoteAsync(
        string contractKey,
        decimal bid,
        decimal ask,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Discover owners with pending conditionals on this contract -- background, so the R-20 filter is bypassed.
        List<Guid> owners = await _discovery.ConditionalOrders
            .IgnoreQueryFilters()
            .Where(order => order.Status == ConditionalStatus.Pending && order.Instrument == contractKey)
            .Select(order => order.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return 0;
        }

        int acted = 0;
        foreach (Guid owner in owners)
        {
            // Per-owner context: the reused ComposeAsync ladder is R-20-scoped, so it sees this owner's account,
            // risk profile, connection, and conventions -- exactly as the operator's own request would.
            await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

            List<ConditionalOrderRecord> pending = await database.ConditionalOrders
                .Where(order => order.Status == ConditionalStatus.Pending && order.Instrument == contractKey)
                .ToListAsync(cancellationToken);

            bool changed = false;
            foreach (ConditionalOrderRecord record in pending)
            {
                changed |= await ProcessOneAsync(database, record, contractKey, bid, ask, now, cancellationToken);
                if (record.Status != ConditionalStatus.Pending || record.FiredOrderId is not null)
                {
                    acted++;
                }
            }

            if (changed)
            {
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        return acted;
    }

    private async Task<bool> ProcessOneAsync(
        TradingCopilotDbContext database,
        ConditionalOrderRecord record,
        string contractKey,
        decimal bid,
        decimal ask,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ConditionalOrder conditional = record.ToConditionalOrder();

        // An entry transacts on its own side: a long buys the ask, a short sells the bid.
        Price price = new(record.Side == OrderSide.Buy ? ask : bid);

        if (conditional.ShouldCancel(price, now))
        {
            // Expiry is time; anything else is an adverse drift past the cancel band.
            record.Status = record.ExpiresAt is { } expiry && now >= expiry
                ? ConditionalStatus.Expired
                : ConditionalStatus.Cancelled;
            _logger.LogInformation(
                "Conditional order {Id} on {Contract} moved to {Status}.", record.Id, contractKey, record.Status);
            return true;
        }

        if (!conditional.ShouldFire(price))
        {
            return false;
        }

        // Fire: the authoritative fire-time re-gate (R-12 / R-5 / R-16), the same ladder the operator's take runs.
        (OrderEndpoints.Composition? composed, _) = await OrderEndpoints.ComposeAsync(
            record.AccountId, database, _venueFactory, _projectXOptions, _executionOptions, _environment, cancellationToken);
        if (composed is null)
        {
            // A precondition failed (e.g. the account is not flat) -- leave it pending and re-decide next quote.
            _logger.LogInformation(
                "Conditional order {Id} triggered but could not be composed yet; still pending.", record.Id);
            return false;
        }

        (ExecutionRequest? request, _) = await OrderEndpoints.BuildRequestAsync(
            composed, record.Symbol ?? record.Instrument, record.TickSize, record.PointValue, record.Side, record.Size,
            record.EntryPrice, record.WorkingStopPrice, record.SafetyStopPrice, record.ReferencePrice,
            record.TakeProfitPrice, record.Type, cancellationToken);
        if (request is null)
        {
            return false;
        }

        ExecutionResult result = await composed.Execution.SendAsync(request, cancellationToken);
        OwnerUser owner = new(record.UserId);

        if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
        {
            SendOrderRequest proposal = new(
                record.Symbol ?? record.Instrument, record.TickSize, record.PointValue, record.Side, record.Size,
                record.EntryPrice, record.WorkingStopPrice, record.SafetyStopPrice, record.ReferencePrice,
                record.Type, record.TakeProfitPrice);

            Order journaled = OrderEndpoints.NewOrderRow(
                owner, composed.Account, proposal, contractKey, OrderStatus.Working,
                result.Decision?.ApprovedQuantity ?? record.Size);
            journaled.VenueOrderKey = result.Order.VenueOrderId;
            journaled.PlacedAt = result.Order.AcceptedAt;
            database.Orders.Add(journaled);
            OrderEndpoints.AddStopPlan(database, owner, journaled, _executionOptions.Value.StopPromotionTicks);
            OrderEndpoints.PersistDecision(database, owner, record.AccountId, journaled.Id, result.Decision);

            record.Status = ConditionalStatus.Fired;
            record.FiredOrderId = journaled.Id;
            _logger.LogInformation(
                "Fired conditional order {Id} as order {OrderId} on {Contract}.", record.Id, journaled.Id, contractKey);
            return true;
        }

        // Refused by the fire-time gate: audit the decision, leave it pending, and re-decide on the next quote.
        OrderEndpoints.PersistDecision(database, owner, record.AccountId, orderId: null, result.Decision);
        _logger.LogWarning(
            "Conditional order {Id} triggered but the fire-time gate refused ({Reason}); still pending.",
            record.Id, result.Reason);
        return result.Decision is not null;
    }

    /// <summary>The owning operator, so the reused ladder is R-20-scoped and every journaled row keeps its owner.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
