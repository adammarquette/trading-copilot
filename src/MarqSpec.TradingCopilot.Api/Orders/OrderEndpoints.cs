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
/// The <c>POST /accounts/{id}/orders</c> endpoint (gh#11, S3 increment 1): the #74 gated send path composed at
/// the API. Every guard this project has built fires here, in order — declared risk rules (fail-closed when
/// absent), the credential-key process guard (ADR-0015), the flat-account honesty rule, then
/// <see cref="OrderExecutionService"/>'s own ladder: R-14 mode × environment, account state, mismatch and type
/// refusals, and the R-5/R-16 gate — before anything reaches the venue.
/// </summary>
public static class OrderEndpoints
{
    /// <summary>Maps the order endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/accounts/{id:guid}/orders").RequireAuthorization();
        group.MapPost("/", SendOrderAsync);
        return endpoints;
    }

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
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null)
        {
            return Results.NotFound();
        }

        // No declared limits, no send (gh#10): the gate's input is fail-closed, never a fabricated default.
        RiskProfileRecord? profile = await database.RiskProfiles
            .FirstOrDefaultAsync(candidate => candidate.AccountId == id, cancellationToken);
        if (profile is null)
        {
            return Results.UnprocessableEntity(new
            {
                error = "No risk profile is declared for this account — declare one (PUT /accounts/{id}/risk) before sending. Absence is refusal, not a default.",
            });
        }

        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null)
        {
            return Results.NotFound(new { error = "The account's connection no longer exists." });
        }

        // One credential set per process (ADR-0015) -- the same guard discovery enforces, on the send path.
        string configuredKey = projectXOptions.Value.CredentialKey;
        if (!string.Equals(connection.CredentialKey, configuredKey, StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"This process holds credentials for key '{configuredKey}', not '{connection.CredentialKey}'. "
                    + "One ProjectX credential set per process (ADR-0015); reconfigure or run a process for that key.",
            });
        }

        FirmConventions conventions = await database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
        ITradingVenue venue = venueFactory.Create(conventions);

        // Fresh venue truth, never the persisted snapshot: the balance the floor is measured against, and the
        // roster proof that this account still exists at the venue.
        IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
        VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
        if (venueAccount is null)
        {
            return Results.Conflict(new { error = "The venue no longer reports this account — rediscover before sending." });
        }

        // Increment-1 honesty (gh#11): the venue reports no unrealized/day P&L. Flat is the only state where
        // UnrealizedPnL = 0 is a fact rather than a guess -- and guessing flatters a red day, the dangerous
        // direction. Lifting this requires real P&L sourcing (its own increment).
        IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
        if (positions.Any(position => !position.IsFlat))
        {
            return Results.Conflict(new
            {
                error = "The account has open positions. Increment-1 sends require a flat account: without venue-reported P&L, an open position makes the risk state a guess.",
            });
        }

        ResolvedContract contract = await venue.ResolveContractAsync(InstrumentId.Parse(request.Symbol), cancellationToken);

        OrderProposal proposal;
        try
        {
            proposal = new OrderProposal(
                InstrumentSpec.Create(InstrumentId.Parse(request.Symbol), request.TickSize, request.PointValue),
                request.Side,
                request.Quantity,
                new Price(request.Entry),
                new Price(request.Stop),
                new Price(request.SafetyStop),
                new Price(request.ReferencePrice));
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }

        // Flat account: balance IS equity (unrealized = 0 exactly), and the venue's balance already includes
        // today's realized P&L -- the day's spent budget is inside it, not guessed alongside it.
        AccountRiskState state = new(venueAccount.Balance, UnrealizedPnL: 0m, DayRealizedPnL: 0m);

        // The floor starts from the HIGHER of the declared starting balance and the live balance, capped by the
        // declared lock. Known gap, recorded on gh#11: a high-water mark above both (drawdown after gains)
        // under-states the floor until the runtime floor-tracking increment lands; the declared lock bounds it.
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
        ExecutionResult result = await execution.SendAsync(
            new ExecutionRequest(proposal, contract, venueAccount, risk, request.Type),
            cancellationToken);

        // Persist the audit trail (engineering §9): a decision row for every SIZED attempt; an order row only
        // for transmitted ones -- journaled under the DB mode guard with the account's persisted mode.
        Order? journaled = null;
        if (result.Outcome == ExecutionOutcome.Placed && result.Order is not null)
        {
            journaled = new Order
            {
                Id = Guid.NewGuid(),
                UserId = currentUser.UserId,
                AccountId = account.Id,
                Instrument = contract.Contract.Key,
                Side = request.Side,
                Size = result.Decision?.ApprovedQuantity ?? request.Quantity,
                Type = request.Type,
                LimitPrice = request.Type == OrderType.Limit ? request.Entry : null,
                StopPrice = request.Type is OrderType.Stop or OrderType.StopLimit ? request.Stop : null,
                Status = OrderStatus.Working,
                Mode = account.Mode,
                VenueOrderKey = result.Order.VenueOrderId,
                PlacedAt = result.Order.AcceptedAt,
            };
            database.Orders.Add(journaled);
        }

        if (result.Decision is not null)
        {
            database.GateDecisions.Add(new GateDecisionRecord
            {
                Id = Guid.NewGuid(),
                UserId = currentUser.UserId,
                AccountId = account.Id,
                OrderId = journaled?.Id,
                Outcome = result.Decision.Outcome,
                ApprovedQuantity = result.Decision.ApprovedQuantity,
                BindingLayer = result.Decision.BindingLayer,
                Reason = result.Decision.Reason,
                DecidedAt = DateTimeOffset.UtcNow,
            });
        }

        await database.SaveChangesAsync(cancellationToken);

        SendOrderResponse response = new(
            result.Outcome.ToString(),
            journaled?.Id,
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
}
