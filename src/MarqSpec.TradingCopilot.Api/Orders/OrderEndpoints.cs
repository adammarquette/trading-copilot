using System.Globalization;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// The order endpoints (gh#11): direct send (increment 1), the <b>arm → edit → take</b> flow (increment 2,
/// ADR-0007), and the opt-in <b>send-as-is</b> fast path (gh#181). One composition ladder serves them all —
/// declared risk rules (fail-closed when absent), the credential-key process guard (ADR-0015), the flat-account
/// honesty rule, fresh venue truth — then <see cref="OrderExecutionService"/>'s own checks. Arming evaluates
/// <b>without transmitting</b>; taking re-validates <b>everything, fresh</b> (R-12) before the venue sees anything.
/// Every path records how the order was placed (<see cref="OrderEntryMethod"/>), so a reader can tell them apart.
/// </summary>
public static class OrderEndpoints
{
    /// <summary>Maps the order endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder accountGroup =
            endpoints.MapGroup("/accounts/{id:guid}/orders").RequireAuthorization().WithTags("Orders");
        accountGroup.MapPost("/", SendOrderAsync)
            .WithSummary("Send an order through the full risk gate (direct send).");
        accountGroup.MapPost("/arm", ArmOrderAsync)
            .WithSummary("Arm (stage) an order: evaluate the risk gate without transmitting.");
        accountGroup.MapPost("/send-as-is", SendAsIsOrderAsync)
            .WithSummary("Opt-in fast path: send an order as-is.");
        accountGroup.MapPost("/conditional", CreateConditionalOrderAsync)
            .WithSummary("Create a conditional (triggered) order.");

        RouteGroupBuilder orderGroup = endpoints.MapGroup("/orders/{id:guid}").RequireAuthorization().WithTags("Orders");
        orderGroup.MapPut("/", EditStagedOrderAsync).WithSummary("Edit a staged (armed) order.");
        orderGroup.MapPost("/take", TakeStagedOrderAsync)
            .WithSummary("Take a staged order: re-validate everything fresh, then transmit.");
        orderGroup.MapPost("/reconcile", ReconcileTakingOrderAsync)
            .WithSummary("Reconcile a stranded (mid-take) order against venue truth: adopt if live, release if flat.");
        orderGroup.MapDelete("/", CancelOrderAsync).WithSummary("Cancel a staged or working order.");
        orderGroup.MapPatch("/price", ModifyWorkingOrderPriceAsync).WithSummary("Modify a working order's price.");

        // The conditional-fire path's runtime recovery (gh#589), the sibling of /orders/{id}/reconcile: resolve a
        // conditional stranded Firing by a maybe-live send fault against venue truth -- adopt the fired order if it
        // rests, release the conditional to Pending if nothing rests and the account is flat.
        RouteGroupBuilder conditionalGroup =
            endpoints.MapGroup("/conditionals/{id:guid}").RequireAuthorization().WithTags("Orders");
        conditionalGroup.MapPost("/reconcile", ReconcileFiringConditionalAsync)
            .WithSummary("Reconcile a stranded (mid-fire) conditional against venue truth: adopt if live, release if flat.");

        return endpoints;
    }

    /// <summary>Everything the ladder assembled for one evaluation.</summary>
    internal sealed record Composition(
        Account Account,
        RiskProfileRecord Profile,
        ITradingVenue Venue,
        VenueAccount VenueAccount,
        RiskContext Risk,
        OrderExecutionService Execution);

    internal static async Task<IResult> SendOrderAsync(
        Guid id,
        SendOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        IAccountEntryGuard entryGuard,
        CancellationToken cancellationToken)
    {
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        // A manual ticket the operator authored and sent directly (R-11) — one action, the full gate.
        return await TransmitAsync(
            composed, request, OrderEntryMethod.Manual, currentUser, database, executionOptions, entryGuard, cancellationToken);
    }

    internal static async Task<IResult> SendAsIsOrderAsync(
        Guid id,
        SendOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        IAccountEntryGuard entryGuard,
        CancellationToken cancellationToken)
    {
        // The opt-in fast path (R-11, gh#181): the Approve split-button's "Send as-is" — an operator who has
        // already decided collapses arm → take into one action. It skips the manual review, NEVER the gate: the
        // same ComposeAsync ladder and the same OrderExecutionService.SendAsync run, so the kill switch, R-14
        // mode × environment, the mismatch and order-type refusals, the R-5 gate and R-16 caps all apply
        // unchanged. Only the journal marker differs — SendAsIs, not Manual — so a reader can tell the paths apart.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        return await TransmitAsync(
            composed, request, OrderEntryMethod.SendAsIs, currentUser, database, executionOptions, entryGuard, cancellationToken);
    }

    /// <summary>
    /// The shared transmit tail (gh#181): build the proposal, run it through the enforcing gate + venue, and — on
    /// a placement — journal the order (tagged with <paramref name="entryMethod"/>), stage its hidden stop, and
    /// persist the gate decision. A blocked or refused attempt journals no order row; a <b>sized</b> attempt
    /// always leaves a <see cref="GateDecisionRecord"/>. The transmitted quantity is the gate's approved quantity,
    /// never the requested one — the invariant that keeps the gate enforcing, not advisory.
    /// </summary>
    private static async Task<IResult> TransmitAsync(
        Composition composed,
        SendOrderRequest request,
        OrderEntryMethod entryMethod,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IOptions<ExecutionOptions> executionOptions,
        IAccountEntryGuard entryGuard,
        CancellationToken cancellationToken)
    {
        // Serialize entry-transmits per account (gh#531). Two concurrent direct sends each compose a fresh
        // flat-account snapshot — ComposeAsync reserves nothing for an outstanding working order — so both pass the
        // gate at full size and both transmit, up to twice the approved risk. The guard holds a per-account lock
        // across the whole tail below, so the no-stacking check and the place cannot interleave between two racers.
        // The check runs INSIDE this callback deliberately: under the real guard it is therefore inside the lock.
        return await entryGuard.RunExclusiveAsync(database, composed.Account.Id, async () =>
        {
            // The no-stacking rule (gh#531). Together with the per-account lock above, this stops two concurrent
            // sends from each sizing against the same flat snapshot and both transmitting: the first to reach the
            // venue journals a Working entry, and the second -- serialized behind it by the guard -- sees that entry
            // here and refuses. ComposeAsync sizes AS IF flat (it reserves nothing for a working order, which moves
            // neither positions nor balance until it fills), so an outstanding entry means the account is no longer
            // honestly flat. This is an ALLOW-LIST, fail-CLOSED: it enumerates the states that are safe to ignore and
            // blocks EVERYTHING ELSE, rather than naming the "bad" states and letting the rest fall through to
            // "proceed" (the blacklist trap `.github/copilot-instructions.md` calls out -- a future OrderStatus, or a
            // corrupt/Unknown row, must fail closed, not silently reopen the 2x race; the cancel endpoint's
            // exhaustive status switch below is the same fail-closed shape). Safe to ignore:
            //   * Staged -- server-side only, unsent, no exposure, so a staged ticket never blocks a direct send;
            //   * Filled -- already realised into the balance ComposeAsync reads -- and Cancelled / Rejected -- never
            //     rested -- so a send after a fully-resolved prior order behaves exactly as a first send. (gh#723
            //     caveat: a stranded take adopted Filled over a STILL-OPEN position is the one Filled that is NOT flat,
            //     so this exclusion no longer implies flatness on its own -- the backstop is ComposeAsync's own
            //     venue-position flatness refusal, which blocks ANY entry on a non-flat account BEFORE this check runs.
            //     Do not weaken that flatness refusal without revisiting this Filled exclusion.)
            // Everything else -- Working, PartiallyFilled, Taking, Unknown, and any status added later -- blocks.
            // Taking (a take in flight, held by the gh#530 durable claim) is imminent exposure and now COUNTS
            // (gh#589): it was excluded while a stranded Taking had no recovery -- counting it would then dead-lock
            // the account's send path -- but gh#589 makes a stranded take recoverable (a mid-take fault releases or
            // is surfaced loud, and a Taking row surviving a restart fails the rehydration safe + loud), so counting
            // it is safe and closes the send-vs-take and send-vs-conditional-fire races.
            bool hasOutstandingEntry = await database.Orders.AnyAsync(
                candidate => candidate.AccountId == composed.Account.Id
                    && candidate.Status != OrderStatus.Staged
                    && candidate.Status != OrderStatus.Filled
                    && candidate.Status != OrderStatus.Cancelled
                    && candidate.Status != OrderStatus.Rejected,
                cancellationToken);

            // A mid-fire conditional counts too (gh#589): a Firing conditional is the durable pre-transmit intent of a
            // fire (gh#577) that has NO Order row yet, so the Orders check above cannot see it -- yet it is a maybe-live
            // entry exactly like a Taking row. Counting it closes send-vs-stranded-fire. Safe to count because a
            // stranded Firing is now recoverable (POST /conditionals/{id}/reconcile), not a dead-lock.
            bool hasMidFireConditional = await database.ConditionalOrders.AnyAsync(
                candidate => candidate.AccountId == composed.Account.Id && candidate.Status == ConditionalStatus.Firing,
                cancellationToken);
            if (hasOutstandingEntry || hasMidFireConditional)
            {
                return Results.Conflict(new { error = "This account already has an outstanding order or a conditional mid-fire. A new send requires a clear account: the flat-account gate would size the new order as if that exposure were not there (gh#531 / gh#589)." });
            }

            (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
                composed, request.Symbol, request.TickSize, request.PointValue, request.Side, request.Quantity,
                request.Entry, request.Stop, request.SafetyStop, request.ReferencePrice, request.Target, request.Type, cancellationToken);
            if (executionRequest is null)
            {
                return proposalRefusal!;
            }

            ExecutionResult result = await composed.Execution.SendAsync(executionRequest, cancellationToken);

            Order? journaled = null;
            if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
            {
                journaled = NewOrderRow(
                    currentUser, composed.Account, request, executionRequest.Contract.Contract.Key,
                    OrderStatus.Working, result.Decision?.ApprovedQuantity ?? request.Quantity, entryMethod);
                journaled.VenueOrderKey = result.Order.VenueOrderId;
                journaled.PlacedAt = result.Order.AcceptedAt;
                database.Orders.Add(journaled);
                AddStopPlan(database, currentUser, journaled, executionOptions.Value.StopPromotionTicks);
            }

            PersistDecision(database, currentUser, composed.Account.Id, journaled?.Id, result.Decision);
            await database.SaveChangesAsync(cancellationToken);

            return MapSendResult(result, journaled?.Id);
        }, cancellationToken);
    }

    internal static async Task<IResult> ArmOrderAsync(
        Guid id,
        SendOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        // Arm is send-minus-transmission: the same fail-closed preconditions, the same ladder, no venue order.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, request.Symbol, request.TickSize, request.PointValue, request.Side, request.Quantity,
            request.Entry, request.Stop, request.SafetyStop, request.ReferencePrice, request.Target, request.Type, cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result = composed.Execution.Evaluate(executionRequest);
        if (result.Outcome != ExecutionOutcome.Evaluated || result.Decision is null)
        {
            return Results.Conflict(new { error = result.Reason }); // pre-gate: nothing staged, nothing sized
        }

        // The ticket stages WHATEVER the gate said -- a currently-blocked proposal is exactly what the operator
        // edits until it passes (ADR-0007). Taking it re-decides from scratch anyway (R-12). It starts an
        // ArmedTake; an edit before take reclassifies it a ModifiedTake (R-11 records deviations).
        Order staged = NewOrderRow(
            currentUser, composed.Account, request, executionRequest.Contract.Contract.Key,
            OrderStatus.Staged, request.Quantity, OrderEntryMethod.ArmedTake);
        database.Orders.Add(staged);
        PersistDecision(database, currentUser, composed.Account.Id, staged.Id, result.Decision);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(StagedOrderResponse.From(staged, result.Decision));
    }

    internal static async Task<IResult> CreateConditionalOrderAsync(
        Guid id,
        CreateConditionalOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        // "Send when conditions met" (ADR-0007, gh#176): held local, fired by a watcher on the trigger. Creation
        // runs the same compose ladder + Evaluate as arm -- for immediate feedback -- but transmits nothing; the
        // authoritative gate re-runs at fire time (R-12).
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        SendOrderRequest order = request.Order;
        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, order.Symbol, order.TickSize, order.PointValue, order.Side, order.Quantity,
            order.Entry, order.Stop, order.SafetyStop, order.ReferencePrice, order.Target, order.Type, cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        // A pre-gate refusal (mode, mismatch, wrong-side target) means there is nothing coherent to hold --
        // refuse now. A gate BLOCK still rests: the setup may be viable by the time it triggers (R-12 re-decides).
        ExecutionResult evaluation = composed.Execution.Evaluate(executionRequest);
        if (evaluation.Outcome != ExecutionOutcome.Evaluated || evaluation.Decision is null)
        {
            return Results.Conflict(new { error = evaluation.Reason });
        }

        // The trigger + cancel band are validated by the domain aggregate before anything is stored.
        try
        {
            _ = ConditionalOrder.Create(
                executionRequest.Proposal,
                new ConditionalTrigger(new Price(request.TriggerPrice), request.TriggerDirection),
                request.CancelDrift is { } drift ? new Price(drift) : null,
                request.ExpiresAt);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }

        ConditionalOrderRecord record = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            AccountId = composed.Account.Id,
            Instrument = executionRequest.Contract.Contract.Key,
            Symbol = order.Symbol,
            Side = order.Side,
            Size = order.Quantity,
            Type = order.Type,
            EntryPrice = order.Entry,
            WorkingStopPrice = order.Stop,
            SafetyStopPrice = order.SafetyStop,
            ReferencePrice = order.ReferencePrice,
            TickSize = order.TickSize,
            PointValue = order.PointValue,
            TakeProfitPrice = order.Target,
            TriggerPrice = request.TriggerPrice,
            TriggerDirection = request.TriggerDirection,
            CancelDriftPrice = request.CancelDrift,
            ExpiresAt = request.ExpiresAt,
            Status = ConditionalStatus.Pending,
            Mode = composed.Account.Mode,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.ConditionalOrders.Add(record);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(ConditionalOrderResponse.From(record, evaluation.Decision));
    }

    internal static async Task<IResult> EditStagedOrderAsync(
        Guid id,
        SendOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status != OrderStatus.Staged)
        {
            return Results.Conflict(new { error = "Only a staged order can be edited — this one has left staging." });
        }

        (Composition? composed, IResult? refusal) = await ComposeAsync(
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, request.Symbol, request.TickSize, request.PointValue, request.Side, request.Quantity,
            request.Entry, request.Stop, request.SafetyStop, request.ReferencePrice, request.Target, request.Type, cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result = composed.Execution.Evaluate(executionRequest);
        if (result.Outcome != ExecutionOutcome.Evaluated || result.Decision is null)
        {
            return Results.Conflict(new { error = result.Reason });
        }

        // Every edit re-gates, and the ticket carries the NEW proposal whole -- take re-builds from these fields.
        ApplyProposal(order, request, executionRequest.Contract.Contract.Key);
        // An edited ticket is a deviation from the armed proposal: it takes as a ModifiedTake, not an ArmedTake
        // (R-11 records deviations). Idempotent -- editing an already-modified ticket stays modified.
        order.EntryMethod = OrderEntryMethod.ModifiedTake;
        PersistDecision(database, currentUser, composed.Account.Id, order.Id, result.Decision);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(StagedOrderResponse.From(order, result.Decision));
    }

    internal static async Task<IResult> TakeStagedOrderAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        IStagedOrderClaim claim,
        IAccountEntryGuard entryGuard,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status != OrderStatus.Staged)
        {
            return Results.Conflict(new { error = "Only a staged order can be taken — this one has left staging." });
        }

        // Serialize entry-transmits per account across the whole tail below (gh#589, extending the send path's gh#531
        // lock to the take path). Two concurrent takes of DIFFERENT staged orders, a take racing a direct send, or a
        // take racing a conditional fire each compose a fresh flat-account snapshot -- ComposeAsync reserves nothing
        // for an outstanding order -- so absent this lock each sizes at full risk and both transmit, up to twice the
        // approved risk on one account. gh#530's IStagedOrderClaim arbitrates two takes of the SAME row; it does not
        // stop two takes of DIFFERENT rows, nor a take vs a send / fire -- that is this account-level lock.
        return await entryGuard.RunExclusiveAsync(database, order.AccountId, async () =>
        {
            // The no-stacking rule, run INSIDE the lock (gh#589) -- identical to the send path's. Refuse if the account
            // already holds an entry that is not fully resolved. It runs BEFORE the claim, so the row being taken is
            // still Staged and excludes itself naturally -- no self-exclusion clause needed. Allow-list, fail-CLOSED:
            // it names the states safe to ignore (Staged / Filled / Cancelled / Rejected) and blocks EVERYTHING else --
            // Working, PartiallyFilled, Taking, Unknown, any status added later. Taking now counts (another in-flight or
            // stranded take is imminent / unknown exposure), safe because gh#589 makes a stranded Taking recoverable.
            // gh#723 caveat: the Filled exclusion no longer proves a flat account -- a stranded take adopted Filled over
            // a STILL-OPEN position (POST /orders/{id}/reconcile) is a Filled that is NOT flat. ComposeAsync's own fresh
            // venue-position flatness refusal (called shortly below) is the backstop that blocks a stack on the adopted
            // position; do not weaken it without revisiting this exclusion. (Same note on the send path's copy.)
            bool hasOutstandingEntry = await database.Orders.AnyAsync(
                candidate => candidate.AccountId == order.AccountId
                    && candidate.Status != OrderStatus.Staged
                    && candidate.Status != OrderStatus.Filled
                    && candidate.Status != OrderStatus.Cancelled
                    && candidate.Status != OrderStatus.Rejected,
                cancellationToken);

            // A mid-fire conditional counts too (gh#589), identical to the send path: a Firing conditional (gh#577) is a
            // maybe-live entry with no Order row yet, so the Orders check cannot see it. Counting it closes take-vs-
            // stranded-fire; safe because a stranded Firing is recoverable via POST /conditionals/{id}/reconcile.
            bool hasMidFireConditional = await database.ConditionalOrders.AnyAsync(
                candidate => candidate.AccountId == order.AccountId && candidate.Status == ConditionalStatus.Firing,
                cancellationToken);
            if (hasOutstandingEntry || hasMidFireConditional)
            {
                return Results.Conflict(new { error = "This account already has an outstanding order or a conditional mid-fire. Taking a second entry would size against a flat-account snapshot that ignores the first's exposure (gh#589)." });
            }

            // CLAIM THE ROW BEFORE GOING ANYWHERE NEAR THE VENUE (gh#530), now inside the account lock. Only the
            // database can arbitrate two takes of the same row, so the claim is a conditional UPDATE (Staged->Taking)
            // behind IStagedOrderClaim; it also loses cleanly to a concurrent cancel of the staged ticket
            // (Staged->Cancelled), which does not take this lock. The tracked entity deliberately still reads Staged:
            // Status has TWO writers -- this change tracker and the claim's conditional UPDATE -- and they must never
            // both own it, because ComposeAsync runs its own SaveChanges and DetectChanges re-marks the property. So
            // the claim owns Staged<->Taking, the tracker owns only the terminal write, and every outcome says which.
            if (!await claim.TryClaimAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = "This order is no longer staged — it was taken or cancelled." });
            }

            // R-12: EVERYTHING re-validates against fresh truth -- fresh roster, fresh flat check, fresh gate. The
            // arm-time decision is history, not authorization. Compose + build are pre-venue READS (roster, positions,
            // contract resolve); a THROW here placed NOTHING, so the claim must be released -- gh#589 counts Taking, so
            // a strand for a take that never reached the venue would dead-lock the whole account (the outer catch).
            Composition? composed = null;
            ExecutionRequest? executionRequest = null;
            try
            {
                IResult? refusal;
                (composed, refusal) = await ComposeAsync(
                    order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
                if (composed is null)
                {
                    // A refused take (a closed market, a tripped kill switch) sent nothing -- release, or it strands.
                    await claim.ReleaseAsync(id, CancellationToken.None);
                    return refusal!;
                }

                // The staged row IS the proposal (kept whole at arm/edit); its venue-neutral Symbol re-resolves the
                // contract fresh -- the front month may even have rolled since arming, and R-12 wants today's truth.
                // WorkingStopPrice, not StopPrice: a Limit/Market order has no venue trigger, and rebuilding the stop
                // from the safety stop would silently re-size against a wider stop than the operator armed (gh#134).
                IResult? proposalRefusal;
                (executionRequest, proposalRefusal) = await BuildRequestAsync(
                    composed, order.Symbol ?? order.Instrument, order.TickSize, order.PointValue, order.Side, order.Size,
                    order.EntryPrice, order.WorkingStopPrice, order.SafetyStopPrice,
                    order.ReferencePrice, order.TakeProfitPrice, order.Type, cancellationToken);
                if (executionRequest is null)
                {
                    await claim.ReleaseAsync(id, CancellationToken.None);
                    return proposalRefusal!;
                }

                // The correlation handle (gh#589, mirroring the conditional fire's gh#577): stamp the row's own id so
                // the venue order this take places carries it as a customTag and can be matched back to THIS row by the
                // reconcile endpoint (POST /orders/{id}/reconcile) -- the durable claim's venue-side counterpart.
                // Operator direct sends leave it null (a human is in the loop and a send journals its own new row on
                // success); the take needs it because a fault after the venue accepts but before the key is journaled
                // strands the claim with a live order behind it.
                executionRequest = executionRequest with { CorrelationTag = order.Id.ToString() };
            }
            catch (Exception)
            {
                // A venue read (roster / positions / contract resolve) or DB fault BEFORE the venue was touched --
                // nothing rests. Release the claim (Taking->Staged) so the ticket stays takeable, then rethrow. This
                // also covers an OperationCanceledException during compose (a disconnect that placed nothing). None:
                // the release is a compensating undo that must complete even if the caller's token is already cancelled.
                await claim.ReleaseAsync(id, CancellationToken.None);
                throw;
            }

            ExecutionResult result;
            try
            {
                result = await composed!.Execution.SendAsync(executionRequest!, cancellationToken);
            }
            catch (VenueRefusalException refusal) when (refusal.Kind == VenueRefusalKind.Definitive)
            {
                // A DEFINITIVE venue rejection (gh#629): the venue responded in the negative and placed NOTHING, so --
                // unlike the maybe-live faults in the broad catch below -- it is safe to release the claim back to
                // Staged (re-takeable, so the operator can amend and retry) and return the reason, rather than
                // stranding the row Taking for a human to reconcile. The release is the claim's conditional UPDATE
                // (WHERE Taking), so the two-writers rule holds. Only the adapter's !success classification reaches
                // here; an indeterminate no-id, a timeout, or a transport fault falls through to the broad catch.
                await claim.ReleaseAsync(id, CancellationToken.None);
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                    "Take of order {OrderId} on account {AccountId} was DEFINITIVELY rejected by the venue: {Reason}. "
                    + "Nothing rests, so the ticket is released to Staged for amendment.",
                    order.Id, order.AccountId, refusal.Message);
                return Results.Conflict(new { error = refusal.Message });
            }
            catch (Exception)
            {
                // ANY remaining fault leaves the order MAYBE-LIVE, so the row is LEFT TAKING -- never released
                // (releasing a maybe-live order re-opens the gh#530 double-place, the whole hazard gh#589 exists to
                // close). Only the DEFINITIVE venue rejection is auto-resolved (above, gh#629); everything this broad
                // catch takes is indeterminate by construction and resists "nothing rests":
                //   * a client disconnect / host shutdown (OperationCanceledException, the caller's token) -- interrupted;
                //   * a venue TIMEOUT -- a TaskCanceledException whose token is HttpClient's, NOT the caller's, so it is
                //     NOT the cancelled-caller case and is the canonical maybe-landed failure;
                //   * a transport fault (HttpRequestException);
                //   * an INDETERMINATE VenueRefusalException -- "accepted but returned no order id" (the venue took it
                //     -- LIVE), which the adapter classifies Indeterminate precisely because it must fail toward Taking.
                // Anything unrecognised falls here too and stays indeterminate -- the safe default is structural, not a
                // list. A durable Taking cannot be re-taken (TryClaim requires Staged); the operator resolves it via
                // POST /orders/<id>/reconcile, and a restart flags it (OrderMidTaking). Uncertainty resolves to the
                // safe state (ADR-0013 §9). Surface it loudly.
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                    "Take of order {OrderId} on account {AccountId} faulted at the venue send; an order MAY be live at "
                    + "the venue and unrecorded. The row is left Taking so it cannot be re-taken; resolve it via "
                    + "POST /orders/<id>/reconcile, or the rehydration flags it on restart (gh#589).",
                    order.Id, order.AccountId);
                throw;
            }

            int? armedSizeForDisposition = null;
            if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
            {
                armedSizeForDisposition = order.Size; // operator-armed size, captured BEFORE the gate's approved quantity overwrites it below
                order.Status = OrderStatus.Working;
                order.VenueOrderKey = result.Order.VenueOrderId;
                order.PlacedAt = result.Order.AcceptedAt;
                order.Size = result.Decision?.ApprovedQuantity ?? order.Size;
                AddStopPlan(database, currentUser, order, executionOptions.Value.StopPromotionTicks);
            }
            else
            {
                // Not placed -- a gate refusal that returned WITHOUT touching the venue (a venue rejection throws and is
                // handled above). Release through the CLAIM, not the tracked entity: the tracker still reads Staged, so
                // assigning Staged here changes nothing and EF would emit no UPDATE, leaving the row stuck in Taking.
                await claim.ReleaseAsync(id, CancellationToken.None);
            }

            PersistDecision(database, currentUser, composed!.Account.Id, order.Id, result.Decision);
            // A fault on this final journal AFTER a Placed send leaves the row Taking (never released above) -- the
            // accepted-but-not-journaled dangerous window, resolved via reconcile / rehydration, exactly as intended.
            await database.SaveChangesAsync(cancellationToken);

            // The suggestion disposition (gh#549) is journaled in its OWN save AFTER the order is durably Working above,
            // so a concurrent pass on the same suggestion (not serialized against this take) can never roll the live
            // take back -- gh#455. A direct ticket writes none.
            if (armedSizeForDisposition is { } armedSize)
            {
                await RecordTakeDispositionAsync(
                    database, order, armedSize, loggerFactory.CreateLogger(nameof(OrderEndpoints)), cancellationToken);
            }

            return MapSendResult(result, order.Id);
        }, cancellationToken);
    }

    internal static async Task<IResult> ReconcileTakingOrderAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        WorkingOrderReconciliationService restingOrders,
        PositionReconciliationService positions,
        FillReconciliationService fills,
        IOptions<ExecutionOptions> executionOptions,
        IStagedOrderClaim claim,
        IAccountEntryGuard entryGuard,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // The runtime resolver for a stranded Taking (gh#589): a take whose venue send was interrupted (a client
        // disconnect, or a journal fault after the venue accepted) leaves the row Taking with no venue key -- the
        // no-stacking check counts Taking, so the account is blocked until this resolves it against venue truth. It is
        // the consumer of the customTag the take stamps: the venue order the take placed carries the row's own id.
        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound(); // not ours (R-20) or gone
        }

        if (order.Status != OrderStatus.Taking)
        {
            // Only a stranded Taking is reconcilable. A Working / PartiallyFilled / Filled / Staged / terminal order is
            // resolved through its own paths (cancel, the account-event stream, take) -- this stays a narrow recovery.
            return Results.Conflict(new { error = "Only an order stranded mid-take (Taking) can be reconciled." });
        }

        // Serialize against operator sends / takes and other reconciles on the same account (gh#589): the adopt below
        // journals a Working order (which the no-stacking check counts), and the release returns the ticket to Staged --
        // both must be atomic against a concurrent entry. Blocking is fine here: this is an operator request, not the
        // conditional watcher (which try-locks so it never waits on this).
        return await entryGuard.RunExclusiveAsync(database, order.AccountId, async () =>
        {
            // Re-read under the lock -- a concurrent reconcile (serialized behind us) may have already resolved it.
            // Reload from DB truth: identity resolution would otherwise hand back our stale pre-lock instance.
            await database.Entry(order).ReloadAsync(cancellationToken);
            if (order.Status != OrderStatus.Taking)
            {
                return Results.Conflict(new { error = "This order is no longer mid-take — it was already reconciled." });
            }

            WorkingOrderReconciliation? truth = await restingOrders.ReconcileAsync(
                order.AccountId, DateTimeOffset.UtcNow, cancellationToken);
            if (truth is null)
            {
                return Results.NotFound(); // account not found / not owned (R-20)
            }

            if (truth.Basis == PositionMarkBasis.Unknown)
            {
                // "We could not ask" is NOT "nothing is there" (gh#381). Never resolve a stranded take against an
                // unknown book -- releasing could re-take a live order, adopting could invent a phantom. Retry later.
                return Results.Conflict(new
                {
                    error = "The venue could not be reached; a stranded take cannot be safely reconciled right now. Retry when the venue is reachable.",
                });
            }

            WorkingOrder? resting = truth.Orders.FirstOrDefault(candidate => candidate.CustomTag == id.ToString());
            if (resting is not null)
            {
                // The take DID place -- adopt the live order so it is tracked (cancellable, kill-switch-sweepable,
                // orphan-guarded by its venue key) and protected (a promotion plan). This is the terminal Taking->Working
                // write the tracker owns; the venue view carries no timestamp, so PlacedAt is stamped at the reconcile
                // instant, and Size takes venue truth (what actually rests).
                int submittedSize = order.Size; // operator-armed size, captured BEFORE venue truth overwrites it below
                order.Status = OrderStatus.Working;
                order.VenueOrderKey = resting.VenueOrderKey;
                order.PlacedAt = DateTimeOffset.UtcNow;
                order.Size = resting.Size;
                AddStopPlan(database, currentUser, order, executionOptions.Value.StopPromotionTicks);
                await database.SaveChangesAsync(cancellationToken);

                // Adopting the live order is the point a stranded suggestion-armed take is finally confirmed placed, so
                // the disposition is written here too (gh#549) — in its OWN save AFTER the adopt above, so it can never
                // abort the adopt (gh#455); the pre-check / catch skips it if the take's own send already journaled one
                // before the strand, or a concurrent pass wrote one.
                await RecordTakeDispositionAsync(
                    database, order, submittedSize, loggerFactory.CreateLogger(nameof(OrderEndpoints)), cancellationToken);

                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                    "Reconciled stranded take {OrderId} on account {AccountId}: the venue order {VenueKey} was live and "
                    + "is adopted as Working (gh#589).",
                    order.Id, order.AccountId, resting.VenueOrderKey);
                return Results.Ok(new { order.Id, status = order.Status.ToString(), adopted = true, venueOrderKey = order.VenueOrderKey });
            }

            // Nothing rests under this row's tag. But "no working order" is NOT "nothing live" -- the take may have
            // FILLED before this reconcile (a filled entry is no longer a working order, and its protective bracket legs
            // carry no customTag). So before releasing, confirm the account is FLAT; otherwise a fill may be this take's,
            // and releasing (Taking->Staged) would strand an untracked open position (gh#589 round-2 review).
            PositionReconciliation? positionTruth = await positions.ReconcileAsync(
                order.AccountId, DateTimeOffset.UtcNow, cancellationToken);
            if (positionTruth is null)
            {
                return Results.NotFound();
            }

            if (positionTruth.Basis == PositionMarkBasis.Unknown)
            {
                return Results.Conflict(new
                {
                    error = "Nothing rests under this order's tag, but the venue could not be reached to confirm the account is flat. Retry when the venue is reachable.",
                });
            }

            if (positionTruth.Positions.Any(position => !position.IsFlat))
            {
                // An open position means the take MAY have filled (a fill is no longer a working order, and its bracket
                // legs carry no customTag). Never release over it. Consult fill history for THIS row's tag: it both
                // confirms the position is this take's and carries the venue key the position snapshot cannot (gh#723).
                TaggedFillEvidence? openFill = await fills.FindFillAsync(
                    order.AccountId, id.ToString(), order.PlacedAt, cancellationToken);
                if (openFill is null)
                {
                    return Results.NotFound(); // account not found / not owned (R-20)
                }

                if (openFill.VetoesRelease)
                {
                    // Venue fill history POSITIVELY confirms this take filled, and the position is still open -- adopt
                    // it onto the row as Filled so it is tracked, journaled and the account unblocked, rather than left
                    // stuck Taking and loud (gh#723 -- the likeliest strand). Size + key come from venue truth (the
                    // fill), never from what the app believes it sent. NO StopPlan: the open position rides the NATIVE
                    // safety bracket the venue attached on fill (a position is never opened unprotected, gh#589), so a
                    // synthetic promotion plan would place a SECOND native stop over that leg -- the round-tripped
                    // sibling below writes none for the analogous reason (there nothing is left to protect; here the
                    // venue already protects it). The account is not flat, but the open position blocks a concurrent
                    // take through the send path's own flatness refusal, and this whole method holds the entry lock, so
                    // adoption opens no stacking window (gh#531/#589).
                    int submittedSize = order.Size; // operator-armed size, captured BEFORE venue truth overwrites it below
                    order.Status = OrderStatus.Filled;
                    order.VenueOrderKey = openFill.VenueOrderKey;
                    order.PlacedAt = DateTimeOffset.UtcNow;
                    order.Size = (int)openFill.FilledSize;
                    await database.SaveChangesAsync(cancellationToken);

                    // A confirmed fill IS a taken suggestion (gh#549): journal the disposition, as the adopt-live branch
                    // does, in its OWN save AFTER the adopt so it can never abort it (gh#455). Unlike the round-tripped
                    // branch (a closed trade), the position here is LIVE, so the disposition belongs with it (R-9).
                    await RecordTakeDispositionAsync(
                        database, order, submittedSize, loggerFactory.CreateLogger(nameof(OrderEndpoints)), cancellationToken);

                    loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                        "Reconciled stranded take {OrderId} on account {AccountId}: nothing rests, but venue fill history "
                        + "shows this tag FILLED {FilledSize} with an OPEN position -- the take executed and is adopted "
                        + "Filled (the native bracket protects the position; no synthetic stop is written) (gh#723).",
                        order.Id, order.AccountId, openFill.FilledSize);
                    return Results.Ok(new
                    {
                        order.Id,
                        status = order.Status.ToString(),
                        adopted = true,
                        filled = true,
                        positionOpen = true,
                        venueOrderKey = order.VenueOrderKey,
                    });
                }

                // The position is open but fill history does NOT positively attribute it to this take (Unavailable --
                // "we could not ask"; NoFillFound -- a negative existence claim that authorises nothing; Unsupported --
                // this venue cannot answer). None may adopt (stamping a possibly-wrong venue key) and none may release
                // (over a possibly-live position), so the pre-gh#723 refusal stands: leave the row Taking, loud, for the
                // operator to resolve the position first (protected by its native bracket meanwhile). gh#723 only ADDS
                // the positive-fill adoption above; every other answer keeps the gh#589 round-2 behaviour.
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                    "Reconcile of stranded take {OrderId} on account {AccountId} found nothing resting under its tag but "
                    + "an OPEN position; fill history ({FillStatus}) does not positively attribute it to this take, so the "
                    + "row is left Taking (not adopted, not released) (gh#723).",
                    order.Id, order.AccountId, openFill.Status);
                return Results.Conflict(new
                {
                    error = "Nothing rests under this order's tag, but the account has an open position that could not be "
                        + "confirmed as this take's fill. Resolve the position before reconciling this ticket.",
                });
            }

            // Flat and reachable, nothing resting -- which is NOT the same as "the take never placed". A take that
            // placed, filled and round-tripped (its bracket closed the position) looks exactly like this. Releasing it
            // is not dangerous the way the conditional's re-arm is -- Staged is inert, so nothing re-transmits -- but
            // it silently DISCARDS a trade that really happened, and the journal is the system's memory (R-8/R-9). So
            // ask fill history before releasing (gh#631).
            // PlacedAt is stamped when the ticket is armed, i.e. before any transmit, so it always precedes the
            // attempt this is searching for. A window that starts too early is safe here; one that starts too late
            // would miss the fill and let the release proceed.
            TaggedFillEvidence? takeFill = await fills.FindFillAsync(
                order.AccountId, id.ToString(), order.PlacedAt, cancellationToken);
            if (takeFill is null)
            {
                return Results.NotFound(); // account not found / not owned (R-20)
            }

            if (takeFill.VetoesRelease)
            {
                // It filled and round-tripped. Journal the fill onto this row rather than handing the ticket back as
                // if nothing happened. No StopPlan: the account is provably flat, so there is no live position left.
                order.Status = OrderStatus.Filled;
                order.VenueOrderKey = takeFill.VenueOrderKey;
                order.PlacedAt = DateTimeOffset.UtcNow;
                order.Size = (int)takeFill.FilledSize;
                await database.SaveChangesAsync(cancellationToken);

                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                    "Reconciled stranded take {OrderId} on account {AccountId}: nothing rests and the account is flat, "
                    + "but venue fill history shows this tag FILLED {FilledSize} -- the take executed and round-tripped. "
                    + "The ticket is journaled Filled rather than released to Staged (gh#631).",
                    order.Id, order.AccountId, takeFill.FilledSize);
                return Results.Ok(new
                {
                    order.Id,
                    status = order.Status.ToString(),
                    adopted = true,
                    filled = true,
                    venueOrderKey = order.VenueOrderKey,
                });
            }

            if (takeFill.Status == TaggedFillStatus.Unavailable)
            {
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                    "Reconcile of stranded take {OrderId} on account {AccountId} found nothing resting and a flat "
                    + "account, but the venue's fill history could not be read -- the ticket is left Taking rather than "
                    + "released on an unknown (gh#631).",
                    order.Id, order.AccountId);
                return Results.Conflict(new
                {
                    error = "Nothing rests under this order's tag and the account is flat, but the venue's fill history "
                        + "could not be read, so it cannot be confirmed that the take never executed. Retry when the "
                        + "venue is reachable.",
                });
            }

            // The venue positively reports no fill, or has no fill history to consult. Release the claim
            // (Taking->Staged) through the CLAIM (its conditional UPDATE owns Staged<->Taking), so the ticket is
            // takeable again; the tracker keeps reading Taking, which is fine as we return without saving it.
            await claim.ReleaseAsync(id, cancellationToken);
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogInformation(
                "Reconciled stranded take {OrderId} on account {AccountId}: nothing rests at the venue under its tag and "
                + "the account is flat; the ticket is released to Staged (gh#589).",
                order.Id, order.AccountId);
            return Results.Ok(new { order.Id, status = OrderStatus.Staged.ToString(), adopted = false });
        }, cancellationToken);
    }

    internal static async Task<IResult> ReconcileFiringConditionalAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        WorkingOrderReconciliationService restingOrders,
        PositionReconciliationService positions,
        FillReconciliationService fills,
        IOptions<ExecutionOptions> executionOptions,
        IAccountEntryGuard entryGuard,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // The runtime resolver for a stranded Firing conditional (gh#589), the sibling of ReconcileTakingOrderAsync: a
        // fire whose venue send faulted (a timeout, a transport fault, a disconnect, or a journal fault after the venue
        // accepted) leaves the conditional Firing with no Order row -- the no-stacking check now counts Firing, so the
        // account is blocked until this resolves it against venue truth. It consumes the customTag the fire stamps: the
        // venue order the fire placed carries the CONDITIONAL's own id (gh#577).
        ConditionalOrderRecord? conditional = await database.ConditionalOrders.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (conditional is null)
        {
            return Results.NotFound(); // not ours (R-20) or gone
        }

        if (conditional.Status != ConditionalStatus.Firing)
        {
            // Only a stranded Firing is reconcilable. A Pending / Fired / Cancelled / Expired conditional is resolved
            // through its own paths (the watcher fires / cancels / expires it; a fire journals its own Order) -- this
            // stays a narrow recovery, exactly like the take reconcile's Taking-only guard.
            return Results.Conflict(new { error = "Only a conditional stranded mid-fire (Firing) can be reconciled." });
        }

        // Serialize against operator sends / takes, the fire watcher, and other reconciles on the same account (gh#589):
        // the adopt below journals a Working order (which the no-stacking check counts) and the release returns the
        // conditional to Pending -- both must be atomic against a concurrent entry. Blocking is fine: this is an operator
        // request, not the fire watcher (which try-locks so it never waits on this).
        return await entryGuard.RunExclusiveAsync(database, conditional.AccountId, async () =>
        {
            // Re-read under the lock -- a concurrent reconcile (serialized behind us) may have already resolved it.
            await database.Entry(conditional).ReloadAsync(cancellationToken);
            if (conditional.Status != ConditionalStatus.Firing)
            {
                return Results.Conflict(new { error = "This conditional is no longer mid-fire — it was already reconciled." });
            }

            WorkingOrderReconciliation? truth = await restingOrders.ReconcileAsync(
                conditional.AccountId, DateTimeOffset.UtcNow, cancellationToken);
            if (truth is null)
            {
                return Results.NotFound(); // account not found / not owned (R-20)
            }

            if (truth.Basis == PositionMarkBasis.Unknown)
            {
                // "We could not ask" is NOT "nothing is there" (gh#381). Never resolve a stranded fire against an
                // unknown book -- releasing could re-fire a live order, adopting could invent a phantom. Retry later.
                return Results.Conflict(new
                {
                    error = "The venue could not be reached; a stranded conditional fire cannot be safely reconciled right now. Retry when the venue is reachable.",
                });
            }

            WorkingOrder? resting = truth.Orders.FirstOrDefault(candidate => candidate.CustomTag == id.ToString());
            if (resting is not null)
            {
                // The fire DID place -- adopt the live order. Unlike the take reconcile (which flips an EXISTING Staged
                // row to Working), a stranded fire journaled NO Order row (it faulted before its success journal), so
                // adopt CREATES the Working row the fire would have, marks the conditional Fired, and links them -- the
                // fire's success journal, run now against venue truth. Size takes venue truth (what actually rests); the
                // venue view carries no timestamp, so PlacedAt is stamped at the reconcile instant. No fresh gate
                // decision is journaled (the order is already live -- there is nothing to re-decide), exactly as the
                // take reconcile adopts without re-gating.
                Account? account = await database.Accounts.FirstOrDefaultAsync(
                    candidate => candidate.Id == conditional.AccountId, cancellationToken);
                if (account is null)
                {
                    return Results.NotFound();
                }

                SendOrderRequest proposal = new(
                    conditional.Symbol ?? conditional.Instrument, conditional.TickSize, conditional.PointValue,
                    conditional.Side, conditional.Size, conditional.EntryPrice, conditional.WorkingStopPrice,
                    conditional.SafetyStopPrice, conditional.ReferencePrice, conditional.Type, conditional.TakeProfitPrice);
                Order journaled = NewOrderRow(
                    currentUser, account, proposal, conditional.Instrument, OrderStatus.Working, resting.Size,
                    OrderEntryMethod.Conditional);
                journaled.VenueOrderKey = resting.VenueOrderKey;
                journaled.PlacedAt = DateTimeOffset.UtcNow;
                database.Orders.Add(journaled);
                AddStopPlan(database, currentUser, journaled, executionOptions.Value.StopPromotionTicks);

                conditional.Status = ConditionalStatus.Fired;
                conditional.FiredOrderId = journaled.Id;
                await database.SaveChangesAsync(cancellationToken);

                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                    "Reconciled stranded fire {ConditionalId} on account {AccountId}: the venue order {VenueKey} was live "
                    + "and is adopted as Working order {OrderId}; the conditional is Fired (gh#589).",
                    conditional.Id, conditional.AccountId, resting.VenueOrderKey, journaled.Id);
                return Results.Ok(new
                {
                    conditionalId = conditional.Id,
                    orderId = journaled.Id,
                    status = conditional.Status.ToString(),
                    adopted = true,
                    venueOrderKey = journaled.VenueOrderKey,
                });
            }

            // Nothing rests under this conditional's tag. But "no working order" is NOT "nothing live" -- the fire may
            // have FILLED before this reconcile (a filled entry is no longer a working order, and its bracket legs carry
            // no customTag). So before releasing, confirm the account is FLAT; otherwise a fill may be this fire's, and
            // releasing (Firing -> Pending) would let the next quote re-fire over an untracked open position.
            PositionReconciliation? positionTruth = await positions.ReconcileAsync(
                conditional.AccountId, DateTimeOffset.UtcNow, cancellationToken);
            if (positionTruth is null)
            {
                return Results.NotFound();
            }

            if (positionTruth.Basis == PositionMarkBasis.Unknown)
            {
                return Results.Conflict(new
                {
                    error = "Nothing rests under this conditional's tag, but the venue could not be reached to confirm the account is flat. Retry when the venue is reachable.",
                });
            }

            if (positionTruth.Positions.Any(position => !position.IsFlat))
            {
                // An open position may be this fire's fill. Do NOT release over it -- leave the conditional Firing +
                // loud; the operator resolves the position first (it is protected by its native bracket + auto-flatten
                // meanwhile). Adopting the FILLED position onto a new Order (there is no working order to match) is
                // deferred (gh#631 covers the round-tripped case; adopting a still-OPEN position is not yet built).
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                    "Reconcile of stranded fire {ConditionalId} on account {AccountId} found nothing resting under its "
                    + "tag but an OPEN position on the account -- the fire may have filled. The conditional is left Firing "
                    + "(not released); resolve the position before reconciling it (gh#589).",
                    conditional.Id, conditional.AccountId);
                return Results.Conflict(new
                {
                    error = "Nothing rests under this conditional's tag, but the account has an open position — the fire may have filled. Resolve the position before reconciling this conditional.",
                });
            }

            // Flat and reachable, nothing resting. Before releasing, close the gh#622 gap: "nothing rests + flat"
            // cannot by itself tell a fire that NEVER PLACED (a timeout that did not land -- the common case, where
            // re-arming is correct) from one that PLACED, FILLED and already ROUND-TRIPPED (its bracket or the
            // auto-flatten closed the position). Both look identical through the resting and position reads, so
            // releasing the second one re-arms a one-shot whose entry already completed -- and because HasFired is a
            // LEVEL test rather than an edge test, the very next quote past the trigger fires it again. That is an
            // unintended second autonomous entry.
            //
            // Fill history is the only thing that separates them, so ask (gh#631).
            TaggedFillEvidence? fill = await fills.FindFillAsync(
                conditional.AccountId, id.ToString(), conditional.CreatedAt, cancellationToken);
            if (fill is null)
            {
                return Results.NotFound(); // account not found / not owned (R-20)
            }

            if (fill.VetoesRelease)
            {
                // The fire DID reach the market and execute. Resolve it as Fired rather than re-arming, and journal
                // the entry that actually happened so the trade is not lost from the record. No StopPlan and no
                // adoption of a live position: we only reach this branch on a provably FLAT account, so this fill has
                // already round-tripped -- there is nothing left to protect.
                Account? filledAccount = await database.Accounts.FirstOrDefaultAsync(
                    candidate => candidate.Id == conditional.AccountId, cancellationToken);
                if (filledAccount is null)
                {
                    return Results.NotFound();
                }

                SendOrderRequest executed = new(
                    conditional.Symbol ?? conditional.Instrument, conditional.TickSize, conditional.PointValue,
                    conditional.Side, conditional.Size, conditional.EntryPrice, conditional.WorkingStopPrice,
                    conditional.SafetyStopPrice, conditional.ReferencePrice, conditional.Type, conditional.TakeProfitPrice);
                Order executedRow = NewOrderRow(
                    currentUser, filledAccount, executed, conditional.Instrument, OrderStatus.Filled,
                    (int)fill.FilledSize, OrderEntryMethod.Conditional);
                executedRow.VenueOrderKey = fill.VenueOrderKey;
                executedRow.PlacedAt = DateTimeOffset.UtcNow;
                database.Orders.Add(executedRow);

                conditional.Status = ConditionalStatus.Fired;
                conditional.FiredOrderId = executedRow.Id;
                await database.SaveChangesAsync(cancellationToken);

                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogWarning(
                    "Reconciled stranded fire {ConditionalId} on account {AccountId}: nothing rests and the account is "
                    + "flat, but venue fill history shows this tag FILLED {FilledSize} -- the entry already executed and "
                    + "round-tripped. The conditional is resolved Fired (NOT re-armed) and the entry is journaled as "
                    + "order {OrderId} (gh#631).",
                    conditional.Id, conditional.AccountId, fill.FilledSize, executedRow.Id);
                return Results.Ok(new
                {
                    conditionalId = conditional.Id,
                    orderId = executedRow.Id,
                    status = conditional.Status.ToString(),
                    adopted = true,
                    filled = true,
                    venueOrderKey = executedRow.VenueOrderKey,
                });
            }

            if (fill.Status == TaggedFillStatus.Unavailable)
            {
                // We could ask, and the asking failed. "We could not tell" is never "it did not fill" -- releasing on
                // it would re-arm on a guess. Leave the conditional Firing for the operator, consistent with how both
                // reads above treat an unreachable venue.
                loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                    "Reconcile of stranded fire {ConditionalId} on account {AccountId} found nothing resting and a flat "
                    + "account, but the venue's fill history could not be read -- the conditional is left Firing rather "
                    + "than re-armed on an unknown (gh#631).",
                    conditional.Id, conditional.AccountId);
                return Results.Conflict(new
                {
                    error = "Nothing rests under this conditional's tag and the account is flat, but the venue's fill "
                        + "history could not be read, so it cannot be confirmed that the fire never executed. Retry when "
                        + "the venue is reachable.",
                });
            }

            // Either the venue positively reports no fill under this tag, or it has no fill history to consult
            // (TaggedFillStatus.Unsupported). Neither adds a reason to hold the row, so release exactly as before
            // this read existed -- the account is provably flat, so the release never happens over a live position and
            // any re-fire re-runs the full gate.
            conditional.Status = ConditionalStatus.Pending;
            await database.SaveChangesAsync(cancellationToken);
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogInformation(
                "Reconciled stranded fire {ConditionalId} on account {AccountId}: nothing rests at the venue under its "
                + "tag and the account is flat; the conditional is released to Pending (gh#589).",
                conditional.Id, conditional.AccountId);
            return Results.Ok(new { conditionalId = conditional.Id, status = ConditionalStatus.Pending.ToString(), adopted = false });
        }, cancellationToken);
    }

    internal static async Task<IResult> CancelOrderAsync(
        Guid id,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound(); // not ours (R-20) or gone
        }

        // A cancel is risk-reducing, so it passes through neither the kill switch (which refuses NEW orders only)
        // nor the send ladder (no risk profile, no flat check): pulling an order off the book is always allowed.
        // What it does depends on where the order rests.
        switch (order.Status)
        {
            case OrderStatus.Taking:
                // A take is IN FLIGHT (gh#530). Before the claim, this row still read Staged -- so a cancel landing
                // mid-take wrote Cancelled, and the take then overwrote it back to Working, leaving a live venue
                // order resting on a ticket the operator had just cancelled. Refusing is the honest answer: the
                // outcome is seconds away, and the working order is cancellable once it resolves.
                return Results.Conflict(new
                {
                    error = "This order is being taken right now — wait for it to resolve, then cancel the working order.",
                });

            case OrderStatus.Staged:
                // A staged ticket is server-side only -- nothing at the venue, so discarding it is a plain delete.
                order.Status = OrderStatus.Cancelled;
                await database.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { order.Id, status = order.Status.ToString() });

            case OrderStatus.Working:
                return await CancelWorkingOrderAsync(
                    order, database, venueFactory, projectXOptions, auditLog, loggerFactory, cancellationToken);

            default:
                // Filled / cancelled / rejected -- already done. A no-op would hide a mistaken request.
                return Results.Conflict(new { error = $"A {order.Status} order cannot be cancelled." });
        }
    }

    private static async Task<IResult> CancelWorkingOrderAsync(
        Order order,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (order.VenueOrderKey is null)
        {
            return Results.Conflict(new { error = "This working order has no venue handle to cancel." });
        }

        // A LIGHT resolution of the order's venue -- no risk profile, no flat check, no gate (a cancel needs none of
        // the send ladder), plus the one-credential-set process guard (ADR-0015). Account/connection are R-20-scoped.
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == order.AccountId, cancellationToken);
        if (account is null)
        {
            return Results.NotFound(new { error = "The order's account no longer exists." });
        }

        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null)
        {
            return Results.NotFound(new { error = "The account's connection no longer exists." });
        }

        string configuredKey = projectXOptions.Value.CredentialKey;
        if (!string.Equals(connection.CredentialKey, configuredKey, StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"This process holds credentials for key '{configuredKey}', not '{connection.CredentialKey}' "
                    + "(ADR-0015). Run a process for that key to cancel its orders.",
            });
        }

        FirmConventions conventions = await database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
        ITradingVenue venue = venueFactory.Create(conventions);
        VenueAccountId venueAccount = VenueAccountId.Create(venue.Id, account.VenueAccountKey);

        try
        {
            await venue.CancelOrderAsync(venueAccount, order.VenueOrderKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The venue refused -- typically the order already left the book (filled, or already cancelled). Do NOT
            // force a terminal status: it may have FILLED, not cancelled, and guessing would mislabel it. The
            // account-event stream (gh#219) is the authoritative venue-truth reconciler and advances the real
            // status; the journal is left untouched for it, and the operator is told why.
            return Results.Conflict(new
            {
                order.Id,
                error = $"The venue refused to cancel order {order.VenueOrderKey}: {error.Message}. "
                    + "Its true status reconciles from venue truth (the account-event stream).",
            });
        }

        order.Status = OrderStatus.Cancelled;

        // The entry is gone, so its stop plan protects nothing -- retire it terminally, else the promotion watcher
        // could promote a native stop for a cancelled entry (gh#183 Finding 4). Committed WITH the status; audited
        // as a secondary write after (the audit must never undo the cancel/retire).
        StopPlanRecord? plan = await database.StopPlans
            .FirstOrDefaultAsync(candidate => candidate.OrderId == order.Id
                && (candidate.Staging == StopStaging.Hidden
                    || candidate.Staging == StopStaging.Native
                    || candidate.Staging == StopStaging.Orphaned), cancellationToken);
        StopStaging? retiredFrom = plan?.Staging;
        if (plan is not null)
        {
            plan.Staging = StopStaging.Retired;
        }

        await database.SaveChangesAsync(cancellationToken);
        await AuditCancelSafelyAsync(auditLog, loggerFactory, order, plan, retiredFrom, cancellationToken);

        return Results.Ok(new { order.Id, status = order.Status.ToString() });
    }

    /// <summary>
    /// Modifies a <b>working</b> order in place (gh#259/gh#267/gh#278/gh#292, ADR-0007) — the venue-facing sibling of
    /// the cancel. Unlike a cancel (risk-reducing, gate-exempt), a modify can add risk (a likelier-to-fill entry, a
    /// wider stop, a larger size), so it runs the <b>full</b> send ladder and is re-gated before the venue is
    /// touched: the gate must approve the new terms, or nothing is transmitted (enforcement below the model). It can
    /// move the entry, re-stage the hidden working stop, and/or <b>resize</b> the order; the <b>safety stop</b> is
    /// held invariant on every path. A working-stop-only move with no size change re-stages locally (no venue call).
    /// </summary>
    internal static async Task<IResult> ModifyWorkingOrderPriceAsync(
        Guid id,
        ModifyWorkingOrderRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // At least one dimension must move: the ENTRY (gh#259), the hidden WORKING stop (gh#267), or the SIZE
        // (gh#292) -- in any combination (gh#278). A move that touches the entry or the size goes through the venue
        // path (it reaches the gateway and re-gates); a working-stop-only move with no size change re-stages the
        // hidden plan locally (no venue call -- a hidden stop is not at the venue).
        bool entryChanged = request.EntryPrice is not null;
        bool stopChanged = request.WorkingStopPrice is not null;
        if (!entryChanged && !stopChanged && request.Size is null)
        {
            return Results.BadRequest(new { error = "Supply at least one of entryPrice, workingStopPrice, or size." });
        }

        // A size <= 0 is refused before anything else -- the CHECK(size > 0) floor surfaced as a clean input refusal,
        // never a DB fault. (Input validation, ahead of loading the order.)
        if (request.Size is <= 0)
        {
            return Results.UnprocessableEntity(new { error = "size must be a positive contract count." });
        }

        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound(); // not ours (R-20) or gone
        }

        // Only a WORKING order resting at the venue can be modified. A Staged ticket edits server-only
        // (PUT /orders/{id}); a terminal order has no live resting entry or stop plan to move. A PARTIALLY-filled
        // order is refused here too -- which is exactly the guard that closes the only stale-bracket-size window a
        // resize could face (gh#292): the always-native bracket materialises fresh at the realized fill, and for a
        // 0-filled (Working) order that fill is the whole new size.
        if (order.Status != OrderStatus.Working)
        {
            return Results.Conflict(new { error = $"Only a working order can be modified — this one is {order.Status}." });
        }

        if (order.VenueOrderKey is null)
        {
            return Results.Conflict(new { error = "This working order has no venue handle to modify." });
        }

        // A Size equal to the current size is not a resize; a request whose ONLY effective field is such a no-op has
        // nothing to do (a genuine price move with a redundant same-size Size still proceeds on its price).
        bool sizeChanged = request.Size is { } requestedSize && requestedSize != order.Size;
        if (!entryChanged && !stopChanged && !sizeChanged)
        {
            return Results.BadRequest(new { error = "size equals the order's current size — nothing to change." });
        }

        // The venue path handles an entry reprice and/or a resize (both reach the gateway and re-gate), threading an
        // optional working-stop move; a working-stop-only move with no size change stays on the local re-stage.
        return entryChanged || sizeChanged
            ? await ModifyAtVenueAsync(order, request.EntryPrice, request.WorkingStopPrice, sizeChanged ? request.Size : null,
                request.ReferencePrice, currentUser, database, venueFactory, projectXOptions, executionOptions, environment,
                killSwitch, auditLog, loggerFactory, metrics, cancellationToken)
            : await RestageWorkingStopAsync(order, request.WorkingStopPrice!.Value, currentUser, database, venueFactory,
                projectXOptions, executionOptions, environment, killSwitch, auditLog, loggerFactory, metrics, cancellationToken);
    }

    /// <summary>
    /// Modifies a working order <b>at the venue</b> — repricing the ENTRY (gh#259), <b>resizing</b> it (gh#292), or
    /// both, optionally moving the hidden WORKING stop in the same commit (gh#278). Re-gated against fresh truth: a
    /// reprice at the resting size, a resize at the new size (honouring a gate downsize). The full chain
    /// <c>safety → working → entry</c> is re-validated on the effective geometry <b>before</b> the venue call, so a
    /// crossing can never trip the <c>StopPlan</c> DB CHECK <i>after</i> the venue already moved. Only the entry /
    /// size reach the venue — a hidden working stop is a local promotion target; the safety stop is invariant.
    /// </summary>
    private static async Task<IResult> ModifyAtVenueAsync(
        Order order,
        decimal? newEntry,
        decimal? newWorkingStop,
        int? newSize,
        decimal? referencePrice,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        bool entryMoves = newEntry is not null;
        bool stopMoves = newWorkingStop is not null;
        bool sizeChanges = newSize is not null;

        // The fat-finger band re-measures a NEW ENTRY against the reference (R-16); a move that does not touch the
        // entry (a pure resize, or a resize + working-stop) needs none and reuses the order's stored reference.
        if (entryMoves && referencePrice is null)
        {
            return Results.BadRequest(new
            {
                error = "referencePrice is required when moving the entry — the fat-finger band re-measures against it (R-16).",
            });
        }

        // The EFFECTIVE geometry the gate and the DB CHECK will see: a dimension the request leaves null holds at the
        // order's current value. The requested quantity is the NEW size on a resize, else the resting size.
        decimal safety = order.SafetyStopPrice;
        decimal effEntry = newEntry ?? order.EntryPrice;
        decimal effWorking = newWorkingStop ?? order.WorkingStopPrice;
        decimal effReference = referencePrice ?? order.ReferencePrice;
        int requestedQuantity = newSize ?? order.Size;

        // Moving the working stop onto the safety stop removes it -- a distinct "drop the stop" action, out of scope.
        if (stopMoves && effWorking == safety)
        {
            return Results.UnprocessableEntity(new
            {
                error = "Moving the working stop onto the safety stop removes it — out of scope. The working stop must "
                    + "stay strictly between the safety stop and the entry.",
            });
        }

        // Ordering on the EFFECTIVE geometry, BEFORE the venue -- only when the entry or the working stop actually
        // moves. A pure resize changes no geometry, so the resting order's already-valid ordering stands untouched.
        // Two shapes, per side:
        //   * The working stop moves (gh#278): it must sit STRICTLY between safety and entry (safety → working →
        //     entry), pre-empting CK_StopPlans_SafetyBeyondActual so it can never trip at commit -- AFTER the venue
        //     already moved -- leaving venue and journal desynced.
        //   * Entry moves, stop held (gh#259): both stops must sit below/above the new entry -- and NOT
        //     safety-vs-working, because a coincident-stop order (working == safety, no plan) is a valid state whose
        //     entry may be repriced.
        if (entryMoves || stopMoves)
        {
            bool ordered = order.Side switch
            {
                OrderSide.Buy => stopMoves
                    ? safety < effWorking && effWorking < effEntry
                    : effWorking < effEntry && safety < effEntry,
                OrderSide.Sell => stopMoves
                    ? safety > effWorking && effWorking > effEntry
                    : effWorking > effEntry && safety > effEntry,
                _ => false, // an unrecognised side is refused, never assumed
            };
            if (!ordered)
            {
                return Results.UnprocessableEntity(new
                {
                    error = stopMoves
                        ? "The entry and its stops must sit strictly safety → working → entry (per side) — the move "
                            + "would cross them."
                        : "The new entry must stay on the far side of both stops (per side) — the move would cross them.",
                });
            }
        }

        // The plan (any staging). A COMBINED move that touches the working stop refuses a promoted / orphaned /
        // terminal plan (its stop is a separate venue order, awaiting re-arm, or done); an entry-only move leaves a
        // non-Hidden plan alone. Loaded before the venue call so a refusal costs nothing.
        StopPlanRecord? plan = await database.StopPlans
            .FirstOrDefaultAsync(candidate => candidate.OrderId == order.Id, cancellationToken);
        if (stopMoves && plan is not null && plan.Staging != StopStaging.Hidden)
        {
            return Results.Conflict(new
            {
                order.Id,
                error = plan.Staging switch
                {
                    StopStaging.Native => "The working stop is promoted to a native venue order; moving it is a venue "
                        + "modify, out of scope. Cancel the order to re-stage.",
                    StopStaging.Orphaned => "The connection is down and the stop is orphaned; it re-arms against venue "
                        + "truth on reconnect. Move it after that.",
                    _ => $"The stop plan is {plan.Staging} and cannot be moved.",
                },
            });
        }

        (Composition? composed, IResult? refusal) = await ComposeAsync(
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        // Re-gate against fresh truth (R-12) at the requested quantity (the NEW size on a resize, else the resting
        // size) and the EFFECTIVE geometry: a modify re-passes R-5 / R-16 exactly as the original send did. The gate
        // is fed the effective working stop, so a combined widen is re-validated against the per-trade layer for
        // free; a resize is re-sized (the gate may approve fewer than asked). The safety stop rides unchanged; the
        // gate re-validates both stops are still protective.
        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, order.Symbol ?? order.Instrument, order.TickSize, order.PointValue, order.Side, requestedQuantity,
            effEntry, effWorking, order.SafetyStopPrice, effReference, order.TakeProfitPrice, order.Type,
            cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result;
        try
        {
            result = await composed.Execution.ModifyAsync(executionRequest, order.VenueOrderKey!, cancellationToken, resize: sizeChanges);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The venue refused the modify -- typically the order left the book (filled, or already cancelled). Do
            // NOT force a status: it may have FILLED at the OLD price, and guessing would mislabel it. The
            // account-event stream (gh#219) reconciles the real status; the journal is left untouched for it.
            return Results.Conflict(new
            {
                order.Id,
                error = $"The venue refused to modify order {order.VenueOrderKey}: {error.Message}. "
                    + "Its true status reconciles from venue truth (the account-event stream).",
            });
        }

        // A gate / kill-switch refusal transmitted nothing and rewrites no price -- journal the decision (if the
        // gate sized one) and map it like a send. The reprice never touched the venue or the order row.
        if (result.Outcome != ExecutionOutcome.Modified)
        {
            PersistDecision(database, currentUser, composed.Account.Id, order.Id, result.Decision);
            await database.SaveChangesAsync(cancellationToken);
            return MapSendResult(result, order.Id);
        }

        // TOCTOU: between loading the order and committing, a fill / cancel may have landed via the seam. The venue
        // accepted the modify, but if the local order has already moved terminal, do not rewrite its price -- abort
        // and let the seam own the reconciliation (the gh#183 re-open-race discipline: mutate only what is still
        // what we read). A fresh, untracked read -- the tracked entity still says Working.
        //
        // This is a re-read, NOT an atomic compare-and-swap: a fill committing in the sub-ms window between this
        // read and the save below is an ACCEPTED, benign residue (the gh#263 precedent, gh#270 review). The true
        // fill price always lives on the Fill rows, so the worst case is a journal EntryPrice that lags a fill --
        // not a safety or P&L error (the native bracket protects the fill; P&L derives from fills). Neither closure
        // is proportionate for a LOW journal-accuracy residue: a concurrency token on Order is symmetric and would
        // fault the seam's / cancel's / take's own writes ("global concurrency token is symmetric"), and a scoped
        // ExecuteUpdate CAS is unsupported by the in-memory unit-test provider (the same tradeoff gh#263 took).
        OrderStatus current = await database.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == order.Id)
            .Select(candidate => candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (current != OrderStatus.Working)
        {
            return Results.Conflict(new
            {
                order.Id,
                error = $"The order moved to {current} while the modify was in flight; the venue accepted the "
                    + "modify, but its status reconciles from venue truth.",
            });
        }

        // When the working stop moves onto an existing plan, re-read it is still Hidden (a fill enabling promotion
        // would have changed Status above, so this is belt-and-suspenders).
        if (stopMoves && plan is not null)
        {
            StopStaging? staging = await database.StopPlans
                .AsNoTracking()
                .Where(candidate => candidate.Id == plan.Id)
                .Select(candidate => (StopStaging?)candidate.Staging)
                .FirstOrDefaultAsync(cancellationToken);
            if (staging != StopStaging.Hidden)
            {
                return Results.Conflict(new
                {
                    order.Id,
                    error = "The stop plan left Hidden while the modify was in flight; nothing was changed.",
                });
            }
        }

        // Commit atomically. The entry (+ reference + limit) when it moved; the working stop (+ the journal StopPrice
        // in lockstep for a stop-type order) when it moved; the SIZE -- the GATE-APPROVED quantity, never the asked
        // one -- when it changed; and a HIDDEN plan's entry basis / actual stop in step. A non-Hidden plan is left
        // untouched (and already refused above when the working stop moves). The SAFETY stop is never written.
        decimal oldEntry = order.EntryPrice;
        decimal oldWorking = order.WorkingStopPrice;
        int oldSize = order.Size;
        if (entryMoves)
        {
            ApplyReprice(order, effEntry, effReference);
        }
        if (stopMoves)
        {
            order.WorkingStopPrice = effWorking;
            if (order.Type is OrderType.Stop or OrderType.StopLimit)
            {
                order.StopPrice = effWorking;
            }
        }
        if (sizeChanges)
        {
            // The gate-approved quantity, not the one asked -- a gate downsize is honoured (never silently exceeded),
            // and the same quantity was transmitted to the venue.
            order.Size = result.Decision!.ApprovedQuantity;
        }

        if (plan is { Staging: StopStaging.Hidden })
        {
            if (entryMoves)
            {
                plan.EntryPrice = effEntry;
            }
            if (stopMoves)
            {
                plan.ActualStopPrice = effWorking;
            }
        }
        else if (stopMoves)
        {
            // A move that installs a working stop on a previously-coincident order (no plan) stages a Hidden plan,
            // exactly as placement would have -- always a tightening vs the prior coincident pair.
            AddStopPlan(database, currentUser, order, executionOptions.Value.StopPromotionTicks);
            plan = database.StopPlans.Local.FirstOrDefault(candidate => candidate.OrderId == order.Id);
        }
        else
        {
            // No working-stop move (an entry reprice and/or a pure resize): a non-Hidden (or absent) plan is left
            // untouched, so the audit references none; a Hidden plan is untouched by a pure resize (it has no size).
            plan = plan is { Staging: StopStaging.Hidden } ? plan : null;
        }

        PersistDecision(database, currentUser, composed.Account.Id, order.Id, result.Decision);
        await database.SaveChangesAsync(cancellationToken);
        if (sizeChanges)
        {
            await AuditResizeSafelyAsync(
                auditLog, loggerFactory, order, plan, oldEntry, oldWorking, oldSize, entryMoves, stopMoves, cancellationToken);
        }
        else if (stopMoves)
        {
            await AuditRepriceAndRestageSafelyAsync(auditLog, loggerFactory, order, plan, oldEntry, oldWorking, cancellationToken);
        }
        else
        {
            await AuditModifySafelyAsync(auditLog, loggerFactory, order, plan, oldEntry, cancellationToken);
        }

        return Results.Ok(new
        {
            order.Id,
            status = order.Status.ToString(),
            entryPrice = order.EntryPrice,
            workingStopPrice = order.WorkingStopPrice,
            size = order.Size,
            requestedSize = newSize,
            outcome = result.Decision!.Outcome.ToString(),
        });
    }

    /// <summary>Writes an entry reprice onto the order row (gh#259) — the entry only; side, size, type, and both stops stand.</summary>
    private static void ApplyReprice(Order order, decimal newEntry, decimal referencePrice)
    {
        order.EntryPrice = newEntry;
        order.ReferencePrice = referencePrice;
        // A Limit order's venue limit IS the entry, so it moves with the reprice. A stop-type order's journal
        // StopPrice tracks the (unchanged) working stop per the ApplyProposal convention, so it is left as it is.
        order.LimitPrice = order.Type == OrderType.Limit ? newEntry : order.LimitPrice;
    }

    // The immutable audit entry for an operator reprice (gh#259, gh#220): owned by the order's operator (R-20). A
    // never-filled resting entry, so NOT synthetic_risk (nothing rested on it and the safety stop is untouched);
    // Native placement. Before/After carry the entry-price transition; when a stop plan moved with it, its id rides.
    private static async Task AuditModifySafelyAsync(
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        Order order,
        StopPlanRecord? plan,
        decimal oldEntry,
        CancellationToken cancellationToken)
    {
        string beforeEntry = oldEntry.ToString(CultureInfo.InvariantCulture);
        string afterEntry = order.EntryPrice.ToString(CultureInfo.InvariantCulture);

        AuditRecord record = new()
        {
            UserId = order.UserId,
            Action = AuditAction.OrderModified,
            Placement = AuditPlacement.Native,
            SyntheticRisk = false,
            StopPlanId = plan?.Id,
            Before = beforeEntry,
            After = afterEntry,
            Detail = $"Working order {order.VenueOrderKey} repriced by the operator: entry {beforeEntry} -> {afterEntry}.",
            RecordedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                error, "Audit write failed for the modify of order {Order}; the modify still completed.", order.Id);
        }
    }

    // The immutable audit entry for a COMBINED entry + working-stop move (gh#278, gh#220): owned by the order's
    // operator (R-20), a never-filled resting entry so NOT synthetic_risk. Native placement. Before/After carry the
    // entry-price transition (the primary venue change); the Detail also records the working-stop transition, and the
    // (existing or just-created) plan's id rides.
    private static async Task AuditRepriceAndRestageSafelyAsync(
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        Order order,
        StopPlanRecord? plan,
        decimal oldEntry,
        decimal oldWorking,
        CancellationToken cancellationToken)
    {
        string beforeEntry = oldEntry.ToString(CultureInfo.InvariantCulture);
        string afterEntry = order.EntryPrice.ToString(CultureInfo.InvariantCulture);
        string beforeStop = oldWorking.ToString(CultureInfo.InvariantCulture);
        string afterStop = order.WorkingStopPrice.ToString(CultureInfo.InvariantCulture);

        AuditRecord record = new()
        {
            UserId = order.UserId,
            Action = AuditAction.OrderModified,
            Placement = AuditPlacement.Native,
            SyntheticRisk = false,
            StopPlanId = plan?.Id,
            Before = beforeEntry,
            After = afterEntry,
            Detail = $"Working order {order.VenueOrderKey} entry + working stop moved by the operator: "
                + $"entry {beforeEntry} -> {afterEntry}, working stop {beforeStop} -> {afterStop}.",
            RecordedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                error, "Audit write failed for the combined modify of order {Order}; the modify still completed.", order.Id);
        }
    }

    // The immutable audit entry for a RESIZE (gh#292, gh#220): owned by the order's operator (R-20), a never-filled
    // resting entry so NOT synthetic_risk (nothing rested on it; the safety stop is untouched). Native placement.
    // Before/After carry the size transition (the defining change); the Detail also records any entry / working-stop
    // move that rode with it, and the (existing or just-created) plan's id rides.
    private static async Task AuditResizeSafelyAsync(
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        Order order,
        StopPlanRecord? plan,
        decimal oldEntry,
        decimal oldWorking,
        int oldSize,
        bool entryMoved,
        bool stopMoved,
        CancellationToken cancellationToken)
    {
        string beforeSize = oldSize.ToString(CultureInfo.InvariantCulture);
        string afterSize = order.Size.ToString(CultureInfo.InvariantCulture);

        string detail = $"Working order {order.VenueOrderKey} resized by the operator: size {beforeSize} -> {afterSize}";
        if (entryMoved)
        {
            detail += $", entry {oldEntry.ToString(CultureInfo.InvariantCulture)} -> "
                + order.EntryPrice.ToString(CultureInfo.InvariantCulture);
        }

        if (stopMoved)
        {
            detail += $", working stop {oldWorking.ToString(CultureInfo.InvariantCulture)} -> "
                + order.WorkingStopPrice.ToString(CultureInfo.InvariantCulture);
        }

        detail += ".";

        AuditRecord record = new()
        {
            UserId = order.UserId,
            Action = AuditAction.OrderModified,
            Placement = AuditPlacement.Native,
            SyntheticRisk = false,
            StopPlanId = plan?.Id,
            Before = beforeSize,
            After = afterSize,
            Detail = detail,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                error, "Audit write failed for the resize of order {Order}; the resize still completed.", order.Id);
        }
    }

    /// <summary>
    /// Re-stages a working order's <b>hidden working stop</b> (gh#267) — a <b>local</b> write, since a hidden stop is
    /// a promotion target, not a venue order (it promotes to a native order only once a position opens, gh#263, which
    /// an unfilled working order does not have). Size and the safety stop stay invariant, so every hard risk limit
    /// (all measured at the safety stop) is preserved; the working stop re-gates <b>only</b> when it <i>widens</i>
    /// under <c>SizingBasis.ActualStop</c> — the one enforced layer (per-trade risk) measured at the working stop.
    /// </summary>
    private static async Task<IResult> RestageWorkingStopAsync(
        Order order,
        decimal newWorking,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        IExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        decimal entry = order.EntryPrice;
        decimal safety = order.SafetyStopPrice;

        // Moving the working stop onto the safety stop REMOVES it (a coincident pair has no distinct working stop) --
        // a separate "drop the stop" action, out of scope. Refuse it distinctly from a generic crossing.
        if (newWorking == safety)
        {
            return Results.UnprocessableEntity(new
            {
                error = "Moving the working stop onto the safety stop removes it — out of scope. The working stop must "
                    + "stay strictly between the safety stop and the entry.",
            });
        }

        // Strict, full-chain ordering on the effective geometry BEFORE any venue read or commit: safety -> working ->
        // entry (per side). The gate does NOT enforce safety-beyond-working (only stops-below-entry, gh#259); this is
        // the only backstop, and it pre-empts CK_StopPlans_SafetyBeyondActual so it can never trip at commit.
        bool ordered = order.Side switch
        {
            OrderSide.Buy => safety < newWorking && newWorking < entry,
            OrderSide.Sell => safety > newWorking && newWorking > entry,
            _ => false, // an unrecognised side is refused, never assumed
        };
        if (!ordered)
        {
            return Results.UnprocessableEntity(new
            {
                error = "The working stop must sit strictly beyond the safety stop and inside the entry "
                    + "(safety → working → entry).",
            });
        }

        // Load the plan WITHOUT a staging filter -- a Hidden-only filter would read an Orphaned / Native / Retired
        // plan as "no plan" and mis-route to create, tripping the one-plan-per-order unique index. Only a Hidden plan
        // (or none, for create) can be re-staged locally.
        StopPlanRecord? plan = await database.StopPlans
            .FirstOrDefaultAsync(candidate => candidate.OrderId == order.Id, cancellationToken);
        if (plan is not null && plan.Staging != StopStaging.Hidden)
        {
            return Results.Conflict(new
            {
                order.Id,
                error = plan.Staging switch
                {
                    StopStaging.Native => "The working stop is promoted to a native venue order; re-staging it is a "
                        + "venue modify, out of scope. Cancel the order to re-stage.",
                    StopStaging.Orphaned => "The connection is down and the stop is orphaned; it re-arms against venue "
                        + "truth on reconnect. Re-stage after that.",
                    _ => $"The stop plan is {plan.Staging} and cannot be re-staged.",
                },
            });
        }

        // Re-gate ONLY a WIDEN under SizingBasis.ActualStop. Every HARD limit is measured at the (unchanged) safety
        // stop, so a tighten -- and any move under SizingBasis.SafetyStop, where the working stop is absent from the
        // gate's math -- is provably inside the already-approved envelope and re-stages purely locally (no gate, no
        // kill switch, no flat check, no venue). A WIDEN raises the per-trade-risk layer, which is sized at the
        // working stop under ActualStop; at the resting size that could now exceed the layer, so it must re-pass.
        bool widens = order.Side switch
        {
            OrderSide.Buy => newWorking < order.WorkingStopPrice,
            OrderSide.Sell => newWorking > order.WorkingStopPrice,
            _ => false,
        };
        if (widens)
        {
            RiskProfileRecord? profile = await database.RiskProfiles
                .FirstOrDefaultAsync(candidate => candidate.AccountId == order.AccountId, cancellationToken);
            if (profile is null)
            {
                return Results.UnprocessableEntity(new
                {
                    error = "No risk profile is declared for this account — declare one (PUT /accounts/{id}/risk) "
                        + "before widening a working stop. Absence is refusal, not a default.",
                });
            }

            if (profile.SizingBasis == SizingBasis.ActualStop)
            {
                (Composition? composed, IResult? refusal) = await ComposeAsync(
                    order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken, metrics);
                if (composed is null)
                {
                    return refusal!;
                }

                // Re-gate at the NEW (wider) working stop, entry + size unchanged, WITHOUT transmitting (the arm
                // precedent -- a hidden stop reaches no venue). Reuse the order's stored reference so the fat-finger
                // layer re-checks the unchanged entry and cannot false-block. Size is invariant, so a Resize refuses.
                (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
                    composed, order.Symbol ?? order.Instrument, order.TickSize, order.PointValue, order.Side, order.Size,
                    order.EntryPrice, newWorking, order.SafetyStopPrice, order.ReferencePrice, order.TakeProfitPrice, order.Type,
                    cancellationToken);
                if (executionRequest is null)
                {
                    return proposalRefusal!;
                }

                ExecutionResult evaluation = composed.Execution.Evaluate(executionRequest);
                GateDecision? decision = evaluation.Decision;
                if (evaluation.Outcome != ExecutionOutcome.Evaluated
                    || decision is null
                    || decision.Outcome != GateOutcome.Allowed
                    || decision.ApprovedQuantity != order.Size)
                {
                    PersistDecision(database, currentUser, order.AccountId, order.Id, decision);
                    await database.SaveChangesAsync(cancellationToken);
                    return Results.UnprocessableEntity(new
                    {
                        order.Id,
                        error = "Widening the working stop this far exceeds the per-trade risk at the resting size — "
                            + (decision?.Reason ?? "refused by the risk gate")
                            + ". Tighten the stop, or reduce risk elsewhere first.",
                    });
                }

                // The widen passed the gate at the resting size: journal the sized decision -- a sized gate attempt
                // always leaves a GateDecisionRecord (like every other gate path), and it commits with the re-stage
                // below, so the audit trail carries an APPROVED risk-increase, not only refused ones (gh#267 review).
                PersistDecision(database, currentUser, order.AccountId, order.Id, decision);
            }
        }

        // TOCTOU: re-read Status == Working (a fill would change it -- and a fill is the only thing that could enable
        // promotion, so this also rules out a Hidden -> Native race, gh#263). Then re-read the plan is still Hidden.
        OrderStatus current = await database.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == order.Id)
            .Select(candidate => candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (current != OrderStatus.Working)
        {
            return Results.Conflict(new
            {
                order.Id,
                error = $"The order moved to {current} while the re-stage was in flight; its status reconciles from "
                    + "venue truth (the account-event stream).",
            });
        }

        if (plan is not null)
        {
            StopStaging? staging = await database.StopPlans
                .AsNoTracking()
                .Where(candidate => candidate.Id == plan.Id)
                .Select(candidate => (StopStaging?)candidate.Staging)
                .FirstOrDefaultAsync(cancellationToken);
            if (staging != StopStaging.Hidden)
            {
                return Results.Conflict(new
                {
                    order.Id,
                    error = "The stop plan left Hidden while the re-stage was in flight; nothing was changed.",
                });
            }
        }

        // Commit the re-stage locally (no venue): the working stop on the order (+ the journal StopPrice in lockstep
        // for a stop-type order, per the ApplyProposal convention -- Order.cs: "for a Stop order it equals StopPrice")
        // and the Hidden plan's ActualStopPrice -- or a NEW Hidden plan when the order was placed with a coincident
        // working/safety pair (installing a distinct working stop is always a tightening, so it needs no re-gate).
        decimal oldWorking = order.WorkingStopPrice;
        order.WorkingStopPrice = newWorking;
        if (order.Type is OrderType.Stop or OrderType.StopLimit)
        {
            order.StopPrice = newWorking;
        }

        if (plan is not null)
        {
            plan.ActualStopPrice = newWorking;
        }
        else
        {
            AddStopPlan(database, currentUser, order, executionOptions.Value.StopPromotionTicks);
            plan = database.StopPlans.Local.FirstOrDefault(candidate => candidate.OrderId == order.Id);
        }

        await database.SaveChangesAsync(cancellationToken);
        await AuditRestageSafelyAsync(auditLog, loggerFactory, order, plan, oldWorking, cancellationToken);

        return Results.Ok(new { order.Id, status = order.Status.ToString(), workingStopPrice = order.WorkingStopPrice });
    }

    // The immutable audit entry for a working-stop re-stage (gh#267, gh#220): owned by the order's operator (R-20). A
    // never-filled resting entry, so NOT synthetic_risk (nothing rested on it; the safety stop is untouched). Native
    // placement. Before/After carry the working-stop transition; the (existing or just-created) plan's id rides.
    private static async Task AuditRestageSafelyAsync(
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        Order order,
        StopPlanRecord? plan,
        decimal oldWorking,
        CancellationToken cancellationToken)
    {
        string beforeStop = oldWorking.ToString(CultureInfo.InvariantCulture);
        string afterStop = order.WorkingStopPrice.ToString(CultureInfo.InvariantCulture);

        AuditRecord record = new()
        {
            UserId = order.UserId,
            Action = AuditAction.OrderModified,
            Placement = AuditPlacement.Native,
            SyntheticRisk = false,
            StopPlanId = plan?.Id,
            Before = beforeStop,
            After = afterStop,
            Detail = $"Working order {order.VenueOrderKey} working stop re-staged by the operator: "
                + $"{beforeStop} -> {afterStop}.",
            RecordedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                error, "Audit write failed for the working-stop re-stage of order {Order}; the re-stage still completed.", order.Id);
        }
    }

    // The immutable audit entry for an operator cancel (gh#250, gh#220): owned by the order's operator (R-20), a
    // resting-entry cancel so NOT synthetic_risk (nothing had filled). Native placement -- the order was a native
    // working order; when a stop plan was retired with it, the record carries the plan's id and staging transition.
    private static async Task AuditCancelSafelyAsync(
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        Order order,
        StopPlanRecord? plan,
        StopStaging? retiredFrom,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            // No stop plan to retire (e.g. working stop == safety stop, so none was staged). The cancel itself is
            // journaled via the order status; the audit records the safety-relevant retirement, so there is none.
            return;
        }

        AuditRecord record = new()
        {
            UserId = order.UserId,
            Action = AuditAction.OrderCancelled,
            Placement = AuditPlacement.Native,
            SyntheticRisk = false,
            StopPlanId = plan.Id,
            Before = retiredFrom?.ToString(),
            After = StopStaging.Retired.ToString(),
            Detail = $"Working order {order.VenueOrderKey} cancelled by the operator; its stop plan was retired.",
            RecordedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger(nameof(OrderEndpoints)).LogError(
                error, "Audit write failed for the cancel of order {Order}; the cancel still completed.", order.Id);
        }
    }

    /// <summary>The shared precondition ladder — identical for send, arm, edit, and take (gh#11).</summary>
    internal static async Task<(Composition? Composition, IResult? Refusal)> ComposeAsync(
        Guid accountId,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        CancellationToken cancellationToken,
        IExecutionMetrics? metrics = null)
    {
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return (null, Results.NotFound());
        }

        // A deactivated account is not a usable path to the venue (gh#322, R-17). Soft-delete exists so an operator
        // can retire a login while KEEPING its journal, so the row is still readable here -- and it must therefore be
        // refused explicitly rather than by absence. Checked before the risk profile so a retired account gives the
        // honest reason rather than "no risk profile declared". DELETE /connections/{id} cascades IsActive to the
        // accounts, so this also catches the cascaded rows.
        if (!account.IsActive)
        {
            return (null, Results.Conflict(new
            {
                error = "This account is deactivated and is no longer a usable path to the venue. "
                    + "Reactivating its connection is a separate, deliberate action.",
            }));
        }

        // No declared limits, no evaluation (gh#10): the gate's input is fail-closed, never a fabricated default.
        RiskProfileRecord? profile = await database.RiskProfiles
            .FirstOrDefaultAsync(candidate => candidate.AccountId == accountId, cancellationToken);
        if (profile is null)
        {
            return (null, Results.UnprocessableEntity(new
            {
                error = "No risk profile is declared for this account — declare one (PUT /accounts/{id}/risk) first. Absence is refusal, not a default.",
            }));
        }

        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null)
        {
            return (null, Results.NotFound(new { error = "The account's connection no longer exists." }));
        }

        // The same guard on the connection itself (gh#322) -- the enforcement the credential-rotation endpoint has
        // always had, which this path was missing, so deactivation was enforced on connection management but
        // bypassable for order entry. Checked here in the SHARED composition, so every outbound path built on it
        // (send, send-as-is, arm, edit, take, modify, conditional) inherits it from one place.
        //
        // Deliberately NOT on the cancel path: CancelOrderAsync resolves its own account/connection and is
        // risk-reducing, so an operator can still pull a retired connection's resting orders. Deactivation must
        // retire a send path, never trap live exposure behind it.
        if (!connection.IsActive)
        {
            return (null, Results.Conflict(new
            {
                error = "This account's connection is deactivated and is no longer a usable path to the venue. "
                    + "Reactivating it is a separate, deliberate action.",
            }));
        }

        // One credential set per process (ADR-0015) -- the same guard discovery enforces.
        string configuredKey = projectXOptions.Value.CredentialKey;
        if (!string.Equals(connection.CredentialKey, configuredKey, StringComparison.Ordinal))
        {
            return (null, Results.Conflict(new
            {
                error = $"This process holds credentials for key '{configuredKey}', not '{connection.CredentialKey}'. "
                    + "One ProjectX credential set per process (ADR-0015); reconfigure or run a process for that key.",
            }));
        }

        FirmConventions conventions = await database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
        ITradingVenue venue = venueFactory.Create(conventions);

        // Fresh venue truth, never the persisted snapshot.
        IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
        VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
        if (venueAccount is null)
        {
            return (null, Results.Conflict(new { error = "The venue no longer reports this account — rediscover before evaluating." }));
        }

        // The flat-account honesty rule (gh#11): flat is the only state where UnrealizedPnL = 0 is a fact, not
        // a guess -- and guessing flatters a red day, the dangerous direction.
        IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
        if (positions.Any(position => !position.IsFlat))
        {
            return (null, Results.Conflict(new
            {
                error = "The account has open positions. Sends and stages require a flat account: without venue-reported P&L, an open position makes the risk state a guess.",
            }));
        }

        // Flat account: balance IS equity, and the venue's balance already includes today's realized P&L. The
        // floor starts from the HIGHER of the declared starting balance and the live balance, bounded by the
        // declared lock (high-water tracking is the recorded gh#11 deferral).
        AccountRiskState state = new(venueAccount.Balance, UnrealizedPnL: 0m, DayRealizedPnL: 0m);
        TrailingDrawdown drawdown = profile.ToTrailingDrawdown(Math.Max(profile.StartingBalance, state.Balance));

        // An Undeclared account trades nowhere, and the consistency-window reader now refuses Undeclared outright
        // (gh#746 review). Guard it to an empty window here: this composition is for a send that is refused for being
        // undeclared regardless, so the window is moot -- and a Trade.Mode == Undeclared filter would have matched
        // zero rows anyway, so this is non-behavioral, only fail-fast-safe against the reader's new guard.
        ConsistencyWindow consistencyWindow = account.Mode == TradingMode.Undeclared
            ? ConsistencyWindow.Empty
            : await database.ConsistencyWindowForAccountAsync(account.Id, account.Mode, cancellationToken);

        RiskContext risk = new(
            venueAccount.Id,
            state,
            drawdown,
            profile.ToAccountRiskRules(),
            profile.ToRiskProfile(),
            profile.ToManualCaps(),
            executionOptions.Value.ToSanityCaps(),
            consistencyWindow);

        OrderExecutionService execution = new(
            new RiskGate(), venue, environment.Value, killSwitch, metrics ?? NullExecutionMetrics.Instance);

        return (new Composition(account, profile, venue, venueAccount with { Mode = account.Mode }, risk, execution), null);
    }

    /// <summary>Resolves the contract and builds the execution request; 400 on an invalid proposal.</summary>
    internal static async Task<(ExecutionRequest? Request, IResult? Refusal)> BuildRequestAsync(
        Composition composed,
        string symbol,
        decimal tickSize,
        decimal pointValue,
        OrderSide side,
        int quantity,
        decimal entry,
        decimal stop,
        decimal safetyStop,
        decimal referencePrice,
        decimal? target,
        OrderType type,
        CancellationToken cancellationToken)
    {
        ResolvedContract contract = await composed.Venue.ResolveContractAsync(InstrumentId.Parse(symbol), cancellationToken);

        try
        {
            OrderProposal proposal = new(
                InstrumentSpec.Create(InstrumentId.Parse(symbol), tickSize, pointValue),
                side,
                quantity,
                new Price(entry),
                new Price(stop),
                new Price(safetyStop),
                new Price(referencePrice),
                target is { } takeProfit ? new Price(takeProfit) : null);

            return (new ExecutionRequest(proposal, contract, composed.VenueAccount, composed.Risk, type), null);
        }
        catch (ArgumentException error)
        {
            return (null, Results.BadRequest(new { error = error.Message }));
        }
    }

    /// <summary>Builds an order row carrying the proposal whole — the R-12 rebuild reads it back.</summary>
    internal static Order NewOrderRow(
        ICurrentUser currentUser,
        Account account,
        SendOrderRequest request,
        string contractKey,
        OrderStatus status,
        int size,
        OrderEntryMethod entryMethod)
    {
        Order order = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            AccountId = account.Id,
            Instrument = contractKey,
            Side = request.Side,
            Size = size,
            Type = request.Type,
            Status = status,
            Mode = account.Mode,
            EntryMethod = entryMethod, // how it was placed (R-11) — a manual send is Manual, an armed ticket ArmedTake, ...
            PlacedAt = DateTimeOffset.UtcNow, // provisional at arm; overwritten by the venue's accept when placed
        };
        ApplyProposal(order, request, contractKey);
        return order;
    }

    /// <summary>Writes the proposal's fields onto the row — one place, so arm, edit, and send agree.</summary>
    private static void ApplyProposal(Order order, SendOrderRequest request, string contractKey)
    {
        order.Instrument = contractKey;
        order.Symbol = request.Symbol;
        order.Side = request.Side;
        order.Type = request.Type;
        order.EntryPrice = request.Entry;
        order.WorkingStopPrice = request.Stop; // the protective stop, kept for every type -- the R-12 rebuild reads it
        order.SafetyStopPrice = request.SafetyStop;
        order.ReferencePrice = request.ReferencePrice;
        order.TickSize = request.TickSize;
        order.PointValue = request.PointValue;
        order.LimitPrice = request.Type == OrderType.Limit ? request.Entry : null;
        // The venue TRIGGER -- only Stop/StopLimit orders carry one; distinct from the working stop above.
        order.StopPrice = request.Type is OrderType.Stop or OrderType.StopLimit ? request.Stop : null;
        // The take-profit rides the row whole (gh#173), so take re-builds and re-transmits the target the
        // operator armed -- a Market/Limit ticket carries it just as a stop-type one does.
        order.TakeProfitPrice = request.Target;
        if (order.Status == OrderStatus.Staged)
        {
            order.Size = request.Quantity;
        }
    }

    private static IResult MapSendResult(ExecutionResult result, Guid? orderId)
    {
        // Advisories ride out with every outcome, not only the refusals (gh#407). The whole point of an advisory
        // layer is that it is visible while the operator can still act -- a warning that appears only once the
        // order is refused is not an early warning, and the consistency target's Advisory posture (the migration
        // default) never refuses at all, so on those accounts this is the ONLY signal there is.
        SendOrderResponse response = new(
            result.Outcome.ToString(),
            orderId,
            result.Order?.VenueOrderId,
            result.Decision?.ApprovedQuantity ?? 0,
            result.Decision?.BindingLayer,
            result.Reason,
            result.Decision?.Advisories ?? []);

        return result.Outcome switch
        {
            ExecutionOutcome.Placed => Results.Ok(response),
            ExecutionOutcome.RefusedByRisk => Results.UnprocessableEntity(response),
            _ => Results.Conflict(response), // pre-gate refusals: mode, account state, mismatch, type
        };
    }

    /// <summary>
    /// Records the staged-stop plan for a transmitted entry (ADR-0007, gh#11): the working stop starts
    /// <b>hidden</b>, the safety stop is already native (it rode the entry as the protective bracket).
    /// </summary>
    /// <remarks>
    /// Skipped when the two stops coincide — there is nothing to stage, because the operator's working stop
    /// <i>is</i> the safety stop and it already rests natively. Staging requires the safety stop strictly
    /// beyond the working one, which is exactly what <see cref="StopPlan.Create"/> (and the DB check) enforce.
    /// </remarks>
    internal static void AddStopPlan(
        TradingCopilotDbContext database,
        ICurrentUser currentUser,
        Order order,
        int promotionTicks)
    {
        bool stageable = order.Side switch
        {
            OrderSide.Buy => order.SafetyStopPrice < order.WorkingStopPrice && order.WorkingStopPrice < order.EntryPrice,
            OrderSide.Sell => order.SafetyStopPrice > order.WorkingStopPrice && order.WorkingStopPrice > order.EntryPrice,
            _ => false,
        };

        if (!stageable)
        {
            return;
        }

        database.StopPlans.Add(new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            OrderId = order.Id,
            Side = order.Side,
            EntryPrice = order.EntryPrice,
            ActualStopPrice = order.WorkingStopPrice,
            SafetyStopPrice = order.SafetyStopPrice,
            ProximityMetric = StopProximityMetric.Ticks,
            ProximityValue = promotionTicks,
            Staging = StopStaging.Hidden,
        });
    }

    internal static void PersistDecision(
        TradingCopilotDbContext database,
        ICurrentUser currentUser,
        Guid accountId,
        Guid? orderId,
        GateDecision? decision)
    {
        if (decision is null)
        {
            return; // pre-gate refusal: never sized, no decision exists
        }

        database.GateDecisions.Add(new GateDecisionRecord
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            AccountId = accountId,
            OrderId = orderId,
            Outcome = decision.Outcome,
            ApprovedQuantity = decision.ApprovedQuantity,
            BindingLayer = decision.BindingLayer,
            Reason = decision.Reason,
            // Journalled with the decision so "was the operator warned before the payout was disqualified?" is
            // answerable after the fact (gh#407). Null when nothing was raised, which is also what every row
            // written before the column existed says.
            Advisories = GateAdvisoryJson.Serialize(decision.Advisories),
            DecidedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Journals the operator's take disposition (gh#549, R-8/R-9) <b>after</b> the order is durably Working: exactly
    /// one <see cref="SuggestionDisposition"/> — <see cref="SuggestionDispositionKind.Taken"/> when the submitted
    /// parameters match the suggestion, <see cref="SuggestionDispositionKind.Modified"/> with the recorded deviations
    /// when they do not. It writes in its <b>own</b> unit of work, so it can <b>never</b> abort the order's transaction.
    /// A direct ticket (<see cref="Order.SuggestionId"/> is <see langword="null"/>) writes none.
    /// </summary>
    /// <remarks>
    /// <b>Call this only AFTER the order's Working-flip <c>SaveChangesAsync</c> has committed (the issue's DoD; gh#455 —
    /// a constraint backstops only its transaction's owner).</b> The one-per-suggestion unique index has a writer this
    /// take is <b>not</b> serialized against — <c>PassAsync</c> takes no account lock — so a concurrent pass on the same
    /// suggestion can commit between the pre-check below and this insert. Were the disposition part of the order's
    /// transaction, that race would roll back a take that is LIVE at the venue (the exact hazard gh#455 names). Because
    /// it is a separate save over an already-durable order, it cannot: a rejected insert is caught and swallowed and the
    /// take stands.
    /// <para>
    /// <b>Be exact about what that costs, because it is not merely a missing record</b> (PR #636 review). When a
    /// concurrent pass wins, the suggestion is left permanently journaled <see cref="SuggestionDispositionKind.Passed"/>
    /// — a <b>wrong</b> disposition, with a live taken order sitting behind it — and R-9's taken-vs-suggested history is
    /// incorrect for that suggestion until someone corrects it. The order is never at risk; the journal is. That trade
    /// is still the right one (a wrong journal row beats a rolled-back live take), but it is a real defect to be
    /// surfaced, not an absence. gh#614's Postgres-tier race proof should therefore assert on the <i>resulting
    /// disposition content</i> being wrong, not merely that the take was not aborted.
    /// </para>
    /// The pre-check keeps the common already-disposed case clean
    /// (no exception); the catch covers the race. A missing suggestion (hard-deleted provenance) skips rather than
    /// throws. <paramref name="submittedSize"/> is the operator-armed size, captured by the caller BEFORE the gate's
    /// approved quantity overwrites <see cref="Order.Size"/>, so a gate reduction is never misread as an operator size
    /// deviation. The comparison itself is exact on the persisted decimals (<see cref="SuggestionDisposition.ForTake"/>).
    /// </remarks>
    private static async Task RecordTakeDispositionAsync(
        TradingCopilotDbContext database,
        Order order,
        int submittedSize,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (order.SuggestionId is not { } suggestionId)
        {
            return; // a manual / direct-armed ticket has no suggestion to dispose
        }

        // R-20 auto-scopes this read to the operator; a suggestion that is not theirs is simply not found.
        Suggestion? suggestion = await database.Suggestions.FirstOrDefaultAsync(
            candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            logger.LogWarning(
                "Order {OrderId} was armed from suggestion {SuggestionId}, but the suggestion is no longer present; "
                + "no take disposition is written (gh#549).",
                order.Id, suggestionId);
            return;
        }

        // Keep the common already-disposed case clean (no exception): a suggestion passed before the take completed, or
        // a prior reconcile that already adopted it. The catch below is the backstop for the concurrent race.
        bool alreadyDisposed = await database.SuggestionDispositions.AnyAsync(
            candidate => candidate.SuggestionId == suggestionId, cancellationToken);
        if (alreadyDisposed)
        {
            logger.LogWarning(
                "Suggestion {SuggestionId} already carries a disposition; the take of order {OrderId} adds none "
                + "(gh#549).",
                suggestionId, order.Id);
            return;
        }

        database.SuggestionDispositions.Add(SuggestionDisposition.ForTake(
            suggestion,
            order.EntryPrice,
            order.WorkingStopPrice,
            order.TakeProfitPrice,
            submittedSize,
            DateTimeOffset.UtcNow));

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Lost the insert race with a concurrent, non-serialized disposition writer: a pass on this suggestion
            // takes no account lock, so it can commit between the pre-check above and this insert, and the unique
            // IX_SuggestionDispositions_SuggestionId rejects this second row. The order is ALREADY durably Working
            // (its own save committed before this method was called), so this NEVER fails the take. ForTake cannot
            // emit a CHECK-violating row (its Kind / Deviations / snapshot are internally consistent), so the unique
            // index is the only expected reject. Clear the tracker so the rejected row is detached.
            //
            // What survives is a WRONG journal row, not a gap: the winner's Passed disposition now describes a
            // suggestion with a live taken order behind it (see the remarks). Logged at Warning with the suggestion
            // id so it is findable, because nothing downstream will notice on its own.
            database.ChangeTracker.Clear();
            logger.LogWarning(
                exception,
                "The take disposition for suggestion {SuggestionId} (order {OrderId}) was rejected at save — most "
                + "likely a concurrent pass on the same suggestion, whose disposition now stands and MISDESCRIBES a "
                + "suggestion that was in fact taken. The take itself stands (the order is durably Working); the "
                + "take's disposition is skipped (gh#549).",
                suggestionId, order.Id);
        }
        catch (Exception exception)
        {
            // EVERY failure of this save is journal-only, so none of them may resurface (PR #636 review). By the time
            // this method runs the order is durably Working; the caller wraps it in no further try/catch, so letting
            // anything out would report "take failed" for an order that is in fact live at the venue — the
            // state-mismatch this codebase treats as dangerous on the maybe-live send path just above.
            //
            // Deliberately includes OperationCanceledException: a caller cancelling mid-journal does not un-place the
            // order, so the take has still succeeded and must be reported as such. Narrowing this back to
            // DbUpdateException alone reopens exactly that hole — proven by the cancellation and provider-fault cases
            // in Take_ShouldStandAndSkipTheDisposition_WhenTheDispositionSaveFaults, which go red without this catch.
            database.ChangeTracker.Clear();
            logger.LogWarning(
                exception,
                "The take disposition for suggestion {SuggestionId} (order {OrderId}) could not be journaled. The take "
                + "stands (the order is durably Working) and the failure is not surfaced to the operator; the "
                + "suggestion is left without a disposition (gh#549).",
                suggestionId, order.Id);
        }
    }
}
