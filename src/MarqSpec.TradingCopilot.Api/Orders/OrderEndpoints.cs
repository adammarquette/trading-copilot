using System.Globalization;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
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
        RouteGroupBuilder accountGroup = endpoints.MapGroup("/accounts/{id:guid}/orders").RequireAuthorization();
        accountGroup.MapPost("/", SendOrderAsync);
        accountGroup.MapPost("/arm", ArmOrderAsync);
        accountGroup.MapPost("/send-as-is", SendAsIsOrderAsync);
        accountGroup.MapPost("/conditional", CreateConditionalOrderAsync);

        RouteGroupBuilder orderGroup = endpoints.MapGroup("/orders/{id:guid}").RequireAuthorization();
        orderGroup.MapPut("/", EditStagedOrderAsync);
        orderGroup.MapPost("/take", TakeStagedOrderAsync);
        orderGroup.MapDelete("/", CancelOrderAsync);
        orderGroup.MapPatch("/price", ModifyWorkingOrderPriceAsync);

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
        CancellationToken cancellationToken)
    {
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
        if (composed is null)
        {
            return refusal!;
        }

        // A manual ticket the operator authored and sent directly (R-11) — one action, the full gate.
        return await TransmitAsync(
            composed, request, OrderEntryMethod.Manual, currentUser, database, executionOptions, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        // The opt-in fast path (R-11, gh#181): the Approve split-button's "Send as-is" — an operator who has
        // already decided collapses arm → take into one action. It skips the manual review, NEVER the gate: the
        // same ComposeAsync ladder and the same OrderExecutionService.SendAsync run, so the kill switch, R-14
        // mode × environment, the mismatch and order-type refusals, the R-5 gate and R-16 caps all apply
        // unchanged. Only the journal marker differs — SendAsIs, not Manual — so a reader can tell the paths apart.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
        if (composed is null)
        {
            return refusal!;
        }

        return await TransmitAsync(
            composed, request, OrderEntryMethod.SendAsIs, currentUser, database, executionOptions, cancellationToken);
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
        CancellationToken cancellationToken)
    {
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
        CancellationToken cancellationToken)
    {
        // Arm is send-minus-transmission: the same fail-closed preconditions, the same ladder, no venue order.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        // "Send when conditions met" (ADR-0007, gh#176): held local, fired by a watcher on the trigger. Creation
        // runs the same compose ladder + Evaluate as arm -- for immediate feedback -- but transmits nothing; the
        // authoritative gate re-runs at fire time (R-12).
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
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
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
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

        // R-12: EVERYTHING re-validates against fresh truth -- fresh roster, fresh flat check, fresh gate. The
        // arm-time decision is history, not authorization.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
        if (composed is null)
        {
            return refusal!;
        }

        // The staged row IS the proposal (kept whole at arm/edit); its venue-neutral Symbol re-resolves the
        // contract fresh -- the front month may even have rolled since arming, and R-12 wants today's truth.
        // WorkingStopPrice, not StopPrice: a Limit/Market order has no venue trigger, and rebuilding the stop
        // from the safety stop would silently re-size against a wider stop than the operator armed (gh#134).
        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, order.Symbol ?? order.Instrument, order.TickSize, order.PointValue, order.Side, order.Size,
            order.EntryPrice, order.WorkingStopPrice, order.SafetyStopPrice,
            order.ReferencePrice, order.TakeProfitPrice, order.Type, cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result = await composed.Execution.SendAsync(executionRequest, cancellationToken);

        if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
        {
            order.Status = OrderStatus.Working;
            order.VenueOrderKey = result.Order.VenueOrderId;
            order.PlacedAt = result.Order.AcceptedAt;
            order.Size = result.Decision?.ApprovedQuantity ?? order.Size;
            AddStopPlan(database, currentUser, order, executionOptions.Value.StopPromotionTicks);
        }

        PersistDecision(database, currentUser, composed.Account.Id, order.Id, result.Decision);
        await database.SaveChangesAsync(cancellationToken);

        return MapSendResult(result, order.Id);
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
    /// Reprices a <b>working</b> order in place (gh#259, ADR-0007) — the venue-facing sibling of the cancel. Unlike
    /// a cancel (risk-reducing, gate-exempt), a reprice can add risk, so it runs the <b>full</b> send ladder and is
    /// re-gated at the <b>unchanged</b> size before the venue is touched: the gate must approve the new price at the
    /// current size, or nothing is transmitted (enforcement below the model). Size and the always-native safety
    /// bracket are held invariant — a resize is a separate increment (it must also re-size the bracket).
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound(); // not ours (R-20) or gone
        }

        // Only a WORKING order resting at the venue can be repriced. A Staged ticket edits server-only
        // (PUT /orders/{id}); a terminal order has no live resting entry to move.
        if (order.Status != OrderStatus.Working)
        {
            return Results.Conflict(new { error = $"Only a working order can be modified — this one is {order.Status}." });
        }

        if (order.VenueOrderKey is null)
        {
            return Results.Conflict(new { error = "This working order has no venue handle to modify." });
        }

        // ENTRY-ONLY (gh#259): the reprice moves the entry; SIZE, side, type, the WORKING stop, and the SAFETY stop
        // are all held invariant. Moving the working stop is a separate increment -- it can SEPARATE a coincident
        // working/safety pair (needing a NEW stop plan) or DIVERGE from an already-promoted native stop, neither of
        // which this increment handles. Size-invariance keeps the attached safety bracket's coverage exact.
        decimal newEntry = request.EntryPrice;

        // The new entry must stay on the entry side of the (unchanged) working and safety stops. The gate's
        // protectiveness check enforces this too, but assert it HERE, before the venue is touched: an entry moved
        // ACROSS its own stops would otherwise trip the StopPlan safety-beyond-actual DB CHECK at commit -- AFTER
        // the venue already repriced -- leaving venue and journal desynced with no reconciler for resting price.
        bool ordered = order.Side switch
        {
            OrderSide.Buy => order.WorkingStopPrice < newEntry && order.SafetyStopPrice < newEntry,
            OrderSide.Sell => newEntry < order.WorkingStopPrice && newEntry < order.SafetyStopPrice,
            _ => false, // an unrecognised side is refused, never assumed
        };
        if (!ordered)
        {
            return Results.UnprocessableEntity(new
            {
                error = "The new entry would cross its own stops — a working order's entry must stay on the entry "
                    + "side of its working and safety stops (which this increment does not move).",
            });
        }

        (Composition? composed, IResult? refusal) = await ComposeAsync(
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch, cancellationToken);
        if (composed is null)
        {
            return refusal!;
        }

        // Re-gate the NEW entry at the UNCHANGED size against fresh truth (R-12): a reprice re-passes R-5 / R-16
        // exactly as the original send did. The working and safety stops ride unchanged; the gate re-validates
        // BOTH are still protective relative to the new entry (a non-protective stop is refused by the gate).
        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await BuildRequestAsync(
            composed, order.Symbol ?? order.Instrument, order.TickSize, order.PointValue, order.Side, order.Size,
            newEntry, order.WorkingStopPrice, order.SafetyStopPrice, request.ReferencePrice, order.TakeProfitPrice, order.Type,
            cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result;
        try
        {
            result = await composed.Execution.ModifyAsync(executionRequest, order.VenueOrderKey, cancellationToken);
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
        OrderStatus current = await database.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (current != OrderStatus.Working)
        {
            return Results.Conflict(new
            {
                order.Id,
                error = $"The order moved to {current} while the modify was in flight; the venue accepted the "
                    + "reprice, but its status reconciles from venue truth.",
            });
        }

        // Commit the reprice atomically: the entry (+ reference) on the order, and the plan's entry basis in
        // lockstep. SIZE, the WORKING stop, the SAFETY stop, and STATUS are never written -- entry-only (gh#259).
        decimal oldEntry = order.EntryPrice;
        ApplyReprice(order, newEntry, request.ReferencePrice);

        // Only a HIDDEN plan's entry basis moves with the entry (so a fraction-of-distance promotion band still
        // aims correctly); its ActualStopPrice -- the working stop -- is NOT touched, because the working stop does
        // not move. A NATIVE / ORPHANED plan is left alone: its stop is a separate venue order (or awaiting re-arm)
        // that an entry reprice does not address, so rewriting its journal basis would misrepresent live protection.
        StopPlanRecord? plan = await database.StopPlans
            .FirstOrDefaultAsync(candidate => candidate.OrderId == order.Id
                && candidate.Staging == StopStaging.Hidden, cancellationToken);
        if (plan is not null)
        {
            plan.EntryPrice = newEntry;
        }

        PersistDecision(database, currentUser, composed.Account.Id, order.Id, result.Decision);
        await database.SaveChangesAsync(cancellationToken);
        await AuditModifySafelyAsync(auditLog, loggerFactory, order, plan, oldEntry, cancellationToken);

        return Results.Ok(new
        {
            order.Id,
            status = order.Status.ToString(),
            entryPrice = order.EntryPrice,
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
        CancellationToken cancellationToken)
    {
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return (null, Results.NotFound());
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

        RiskContext risk = new(
            venueAccount.Id,
            state,
            drawdown,
            profile.ToAccountRiskRules(),
            profile.ToRiskProfile(),
            profile.ToManualCaps(),
            executionOptions.Value.ToSanityCaps());

        OrderExecutionService execution = new(new RiskGate(), venue, environment.Value, killSwitch);

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
        SendOrderResponse response = new(
            result.Outcome.ToString(),
            orderId,
            result.Order?.VenueOrderId,
            result.Decision?.ApprovedQuantity ?? 0,
            result.Decision?.BindingLayer,
            result.Reason);

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
            DecidedAt = DateTimeOffset.UtcNow,
        });
    }
}
