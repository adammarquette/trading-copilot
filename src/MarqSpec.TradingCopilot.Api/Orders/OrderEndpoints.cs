using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// The order endpoints (gh#11): direct send (increment 1) and the <b>arm → edit → take</b> flow (increment 2,
/// ADR-0007). One composition ladder serves them all — declared risk rules (fail-closed when absent), the
/// credential-key process guard (ADR-0015), the flat-account honesty rule, fresh venue truth — then
/// <see cref="OrderExecutionService"/>'s own checks. Arming evaluates <b>without transmitting</b>; taking
/// re-validates <b>everything, fresh</b> (R-12) before the venue sees anything.
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
        accountGroup.MapPost("/conditional", CreateConditionalOrderAsync);

        RouteGroupBuilder orderGroup = endpoints.MapGroup("/orders/{id:guid}").RequireAuthorization();
        orderGroup.MapPut("/", EditStagedOrderAsync);
        orderGroup.MapPost("/take", TakeStagedOrderAsync);
        orderGroup.MapDelete("/", CancelStagedOrderAsync);

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
        CancellationToken cancellationToken)
    {
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, cancellationToken);
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

        ExecutionResult result = await composed.Execution.SendAsync(executionRequest, cancellationToken);

        Order? journaled = null;
        if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
        {
            journaled = NewOrderRow(
                currentUser, composed.Account, request, executionRequest.Contract.Contract.Key,
                OrderStatus.Working, result.Decision?.ApprovedQuantity ?? request.Quantity);
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
        CancellationToken cancellationToken)
    {
        // Arm is send-minus-transmission: the same fail-closed preconditions, the same ladder, no venue order.
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, cancellationToken);
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
        // edits until it passes (ADR-0007). Taking it re-decides from scratch anyway (R-12).
        Order staged = NewOrderRow(
            currentUser, composed.Account, request, executionRequest.Contract.Contract.Key,
            OrderStatus.Staged, request.Quantity);
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
        CancellationToken cancellationToken)
    {
        // "Send when conditions met" (ADR-0007, gh#176): held local, fired by a watcher on the trigger. Creation
        // runs the same compose ladder + Evaluate as arm -- for immediate feedback -- but transmits nothing; the
        // authoritative gate re-runs at fire time (R-12).
        (Composition? composed, IResult? refusal) = await ComposeAsync(
            id, database, venueFactory, projectXOptions, executionOptions, environment, cancellationToken);
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
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, cancellationToken);
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
            order.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, cancellationToken);
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

    internal static async Task<IResult> CancelStagedOrderAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Order? order = await database.Orders.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status != OrderStatus.Staged)
        {
            // Cancelling a WORKING order is venue work (a later increment); cancelling a cancelled one is a
            // mistake worth surfacing, not a silent no-op.
            return Results.Conflict(new { error = "Only a staged order can be cancelled here — this one has left staging." });
        }

        order.Status = OrderStatus.Cancelled;
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { order.Id, status = order.Status.ToString() });
    }

    /// <summary>The shared precondition ladder — identical for send, arm, edit, and take (gh#11).</summary>
    internal static async Task<(Composition? Composition, IResult? Refusal)> ComposeAsync(
        Guid accountId,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
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

        OrderExecutionService execution = new(new RiskGate(), venue, environment.Value);

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
        int size)
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
