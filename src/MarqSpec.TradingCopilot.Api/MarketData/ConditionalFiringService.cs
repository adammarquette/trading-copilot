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
/// <para>
/// A fire commits a <b>durable pre-transmit intent</b> (<see cref="ConditionalStatus.Firing"/>, gh#577) before the
/// venue is touched, so a fault in the transmit→journal window — a DB fault, or a shutdown after the venue accepted
/// but before the order journaled — leaves the conditional <b>Firing</b>, not Pending; discovery is Pending-only, so
/// it can never blind-re-fire a live-but-unjournaled order (it is reconciled / surfaced against venue truth on
/// replay, ADR-0013). A venue rejection <i>before</i> acceptance reverts the intent to Pending (the gh#532
/// containment). Each fire's venue order carries the conditional's id as a <c>customTag</c>, so a replay can
/// recognise its own already-placed order rather than transmit a duplicate.
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
    private readonly IKillSwitch _killSwitch;
    private readonly ILogger<ConditionalFiringService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover which owners have pending conditionals.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="venueFactory">The venue factory.</param>
    /// <param name="projectXOptions">The process's ProjectX credential-key configuration.</param>
    /// <param name="executionOptions">The R-16 sanity caps and stop-promotion band.</param>
    /// <param name="environment">The R-14 deployment environment.</param>
    /// <param name="killSwitch">The kill-switch state; a killed system refuses the fire-time send (gh#189).</param>
    /// <param name="logger">The logger.</param>
    public ConditionalFiringService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        ILogger<ConditionalFiringService> logger)
    {
        _discovery = discovery;
        _options = options;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _executionOptions = executionOptions;
        _environment = environment;
        _killSwitch = killSwitch;
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
            // List this owner's pending conditionals on the contract (a read, so the R-20 filter is bypassed as in
            // the owner discovery above), then process EACH in its own unit of work below. A fired conditional
            // stages its order journal, stop plan, decision, and its Fired transition together and commits them on
            // that record's own SaveChanges -- before any sibling is touched -- so a later record's venue fault can
            // never discard an earlier record's already-transmitted order, and one failing record cannot starve the
            // rest (gh#532). This is the per-record independence the stop-promotion watcher already holds; the single
            // batched save this replaces did not.
            List<Guid> pendingIds = await _discovery.ConditionalOrders
                .IgnoreQueryFilters()
                .Where(order => order.UserId == owner && order.Status == ConditionalStatus.Pending && order.Instrument == contractKey)
                .Select(order => order.Id)
                .ToListAsync(cancellationToken);

            foreach (Guid conditionalId in pendingIds)
            {
                acted += await ProcessRecordAsync(owner, conditionalId, contractKey, bid, ask, now, cancellationToken);
            }
        }

        return acted;
    }

    /// <summary>
    /// Processes one pending conditional in its <b>own owner-scoped unit of work</b> (gh#532), committing its work
    /// before any sibling is touched so a peer's fault can never discard an order the venue has already accepted. A
    /// fault is <b>contained</b>: the conditional stays <see cref="ConditionalStatus.Pending"/> and re-decides on the
    /// next quote (ADR-0013's safe "did not fire"), so one poison record neither rolls back a committed peer nor
    /// starves the rest.
    /// </summary>
    /// <returns>1 if the conditional was acted on (fired, cancelled, or expired), otherwise 0.</returns>
    private async Task<int> ProcessRecordAsync(
        Guid owner,
        Guid conditionalId,
        string contractKey,
        decimal bid,
        decimal ask,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool mayBeLiveAtVenue = false;
        try
        {
            // Per-owner context: the reused ComposeAsync ladder is R-20-scoped, so it sees this owner's account,
            // risk profile, connection, and conventions -- exactly as the operator's own request would.
            await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

            ConditionalOrderRecord? record = await database.ConditionalOrders.FirstOrDefaultAsync(
                order => order.Id == conditionalId && order.Status == ConditionalStatus.Pending, cancellationToken);
            if (record is null)
            {
                return 0; // resolved by an earlier pass between discovery and now -- nothing to do
            }

            // Track "the order MAY be live at the venue" from the MOMENT the durable Firing intent commits (inside
            // ProcessOneAsync, before the send), not from the venue's accept: an interruption anywhere from the intent
            // commit through the journal can leave an order resting at the venue but unrecorded, and every such window
            // must be surfaced loudly below -- INCLUDING a shutdown mid-send, whose outcome is unknown and which never
            // reaches the accept (gh#577 review). The two revert-to-Pending paths (a venue rejection, a gate refusal --
            // nothing rests) clear it back to false, so a routine rejection stays quiet.
            FiringOutcome outcome = await ProcessOneAsync(
                database, record, contractKey, bid, ask, now, live => mayBeLiveAtVenue = live, cancellationToken);

            if (outcome != FiringOutcome.Unchanged)
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            return record.Status != ConditionalStatus.Pending || record.FiredOrderId is not null ? 1 : 0;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            // A clean host shutdown -- let it stop the pass rather than swallow it as a per-record fault. But if the
            // cancellation landed AFTER the durable Firing intent committed (the send in flight, its outcome unknown;
            // or the venue accepted and the journal had not yet committed), an order MAY be live at the venue and
            // unrecorded -- the transmit->journal window (gh#577), triggered by shutdown. Key the loud log off that
            // durable intent, not off a venue accept the in-flight case never observes, so THIS window -- the one the
            // ADR calls the dangerous one -- is never silent. The conditional is left Firing so it cannot re-fire.
            if (mayBeLiveAtVenue)
            {
                _logger.LogError(
                    error,
                    "Conditional order {Id} on {Contract} was interrupted by host shutdown after its firing intent "
                    + "committed but before the fire was journaled; an order may be live at the venue and unrecorded. "
                    + "The conditional is left Firing so it cannot re-fire, and is reconciled against venue truth on "
                    + "replay (gh#577).",
                    conditionalId, contractKey);
            }

            throw;
        }
        catch (Exception error) when (mayBeLiveAtVenue)
        {
            // The dangerous window: the venue ACCEPTED the order but its journal did not commit. The order may be
            // live and unrecorded. The durable pre-transmit intent (gh#577) has left the conditional Firing -- not
            // Pending -- so a later quote CANNOT blind-re-fire it; it is reconciled / surfaced against venue truth
            // instead. Surface it loudly; the pass still presses on.
            _logger.LogError(
                error,
                "Conditional order {Id} on {Contract} transmitted an order the venue accepted, but journaling it "
                + "FAILED. The order may be live at the venue and unrecorded; the conditional is left Firing so it "
                + "cannot re-fire, and is reconciled against venue truth on replay (gh#577).",
                conditionalId, contractKey);
            return 0;
        }
        catch (Exception error)
        {
            // Contain a fault BEFORE anything was transmitted (a venue read / compose / gate fault): the conditional
            // stays Pending and re-decides on the next quote, and the pass presses on so one poison record cannot
            // discard a committed peer or starve the rest (gh#532).
            _logger.LogWarning(
                error,
                "Conditional order {Id} on {Contract} could not be processed this quote; it stays pending and "
                + "re-decides on the next quote.",
                conditionalId, contractKey);
            return 0;
        }
    }

    private async Task<FiringOutcome> ProcessOneAsync(
        TradingCopilotDbContext database,
        ConditionalOrderRecord record,
        string contractKey,
        decimal bid,
        decimal ask,
        DateTimeOffset now,
        Action<bool> onMayBeLiveAtVenue,
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
            return FiringOutcome.Resolved;
        }

        if (!conditional.ShouldFire(price))
        {
            return FiringOutcome.Unchanged;
        }

        // Fire: the authoritative fire-time re-gate (R-12 / R-5 / R-16), the same ladder the operator's take runs.
        (OrderEndpoints.Composition? composed, _) = await OrderEndpoints.ComposeAsync(
            record.AccountId, database, _venueFactory, _projectXOptions, _executionOptions, _environment, _killSwitch, cancellationToken);
        if (composed is null)
        {
            // A precondition failed (e.g. the account is not flat) -- leave it pending and re-decide next quote.
            _logger.LogInformation(
                "Conditional order {Id} triggered but could not be composed yet; still pending.", record.Id);
            return FiringOutcome.Unchanged;
        }

        (ExecutionRequest? request, _) = await OrderEndpoints.BuildRequestAsync(
            composed, record.Symbol ?? record.Instrument, record.TickSize, record.PointValue, record.Side, record.Size,
            record.EntryPrice, record.WorkingStopPrice, record.SafetyStopPrice, record.ReferencePrice,
            record.TakeProfitPrice, record.Type, cancellationToken);
        if (request is null)
        {
            return FiringOutcome.Unchanged;
        }

        // The correlation handle (gh#577): stamp the conditional's own id so the venue order this fire places can be
        // matched back to THIS conditional on a replay -- the durable intent's venue-side counterpart. Threaded here
        // (operator paths leave it null; a human is in the loop) rather than through the shared BuildRequestAsync.
        request = request with { CorrelationTag = record.Id.ToString() };

        // Durable pre-transmit intent (gh#577): commit Firing BEFORE the venue is touched. A fault anywhere from here
        // to the journal commit then leaves the conditional Firing -- and discovery / ShouldFire are Pending-only, so
        // it can never blind-re-fire; it is reconciled / surfaced instead (ADR-0013's "never auto-act on rehydrated
        // state -- re-validate first"). This is one extra row-flip on the fire path (per fire, not per quote).
        record.Status = ConditionalStatus.Firing;
        await database.SaveChangesAsync(cancellationToken);

        // The intent is now durable -- from here until the fire is journaled, an order may be resting at the venue but
        // unrecorded, so every interruption (a shutdown mid-send, an accepted-but-unjournaled fire) must surface
        // loudly. Cleared to false only on the revert-to-Pending paths below, where nothing rests.
        onMayBeLiveAtVenue(true);

        OwnerUser owner = new(record.UserId);
        ExecutionResult result;
        try
        {
            result = await composed.Execution.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A shutdown landed mid-send: the venue MAY have accepted the order (the send was interrupted, its
            // outcome unknown). Leave the intent Firing -- reverting could strand a live order and then re-fire it --
            // and let the caller's handler surface it (gh#577). Uncertainty resolves to the safe state (ADR-0013, §9).
            throw;
        }
        catch (Exception)
        {
            // The venue REJECTED the send before accepting it (a routine rejection -- ProjectXVenue throws on
            // !Success), so nothing is resting: clear the may-be-live signal (this is a definitive rejection, not the
            // dangerous window), revert the durable intent to Pending, and re-decide on the next quote -- the gh#532
            // containment, preserved. Rethrow so the per-record handler logs it as a routine skip and the pass presses on.
            //
            // Residual (gh#577): a *transport* fault that in fact landed also surfaces as a throw here and is reverted
            // -- telling a definitive rejection from a maybe-landed transport fault needs a venue-seam signal (a
            // refusal OUTCOME, not a throw), deferred; the customTag handle then lets a reconcile recognise such a
            // duplicate, and the real-Postgres proof of this window is gh#578.
            onMayBeLiveAtVenue(false);
            record.Status = ConditionalStatus.Pending;
            await database.SaveChangesAsync(cancellationToken);
            throw;
        }

        if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
        {
            // The venue has ACCEPTED the order. The conditional is already durably Firing and the may-be-live signal
            // was raised when that intent committed (before the send), so if the journaling below throws, the fault is
            // correctly surfaced as the dangerous "accepted but not journaled" window (gh#577) and it stays Firing.

            SendOrderRequest proposal = new(
                record.Symbol ?? record.Instrument, record.TickSize, record.PointValue, record.Side, record.Size,
                record.EntryPrice, record.WorkingStopPrice, record.SafetyStopPrice, record.ReferencePrice,
                record.Type, record.TakeProfitPrice);

            Order journaled = OrderEndpoints.NewOrderRow(
                owner, composed.Account, proposal, contractKey, OrderStatus.Working,
                result.Decision?.ApprovedQuantity ?? record.Size, OrderEntryMethod.Conditional);
            journaled.VenueOrderKey = result.Order.VenueOrderId;
            journaled.PlacedAt = result.Order.AcceptedAt;
            database.Orders.Add(journaled);
            OrderEndpoints.AddStopPlan(database, owner, journaled, _executionOptions.Value.StopPromotionTicks);
            OrderEndpoints.PersistDecision(database, owner, record.AccountId, journaled.Id, result.Decision);

            record.Status = ConditionalStatus.Fired;
            record.FiredOrderId = journaled.Id;
            _logger.LogInformation(
                "Fired conditional order {Id} as order {OrderId} on {Contract}.", record.Id, journaled.Id, contractKey);
            return FiringOutcome.Transmitted;
        }

        // A clean gate refusal -- the venue was never touched (every SendAsync refusal returns before PlaceOrderAsync),
        // so nothing rests: clear the may-be-live signal, revert the durable intent to Pending, audit the decision, and
        // re-decide on the next quote. Resolved (never Unchanged) so the outer save commits the revert.
        onMayBeLiveAtVenue(false);
        record.Status = ConditionalStatus.Pending;
        OrderEndpoints.PersistDecision(database, owner, record.AccountId, orderId: null, result.Decision);
        _logger.LogWarning(
            "Conditional order {Id} triggered but the fire-time gate refused ({Reason}); reverted to pending.",
            record.Id, result.Reason);
        return FiringOutcome.Resolved;
    }

    /// <summary>What processing one conditional against a quote resolved to — it drives whether to persist, and
    /// whether an order was transmitted (so a journaling failure AFTER a transmit is surfaced as the dangerous case
    /// it is, not swallowed as an ordinary skip).</summary>
    private enum FiringOutcome
    {
        /// <summary>Nothing to persist — not triggered, not yet composable, or a gate refusal with no decision.</summary>
        Unchanged,

        /// <summary>Resolved without transmitting — cancelled, expired, or a gate refusal whose decision is audited.</summary>
        Resolved,

        /// <summary>Fired — the entry was transmitted to the venue and its order + stop plan + decision are staged.</summary>
        Transmitted,
    }

    /// <summary>The owning operator, so the reused ladder is R-20-scoped and every journaled row keeps its owner.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
