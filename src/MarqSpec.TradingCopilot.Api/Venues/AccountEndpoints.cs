using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The <c>/accounts</c> endpoints — the per-account stage override (gh#76). The conservative name-resolver
/// leaves anything it cannot confidently recognise as <c>Unknown → Undeclared</c> (the gh#60 lesson: never
/// guess); this is where the operator supplies the answer. The override is a <b>declaration</b>, so it follows
/// declaration rules: <c>Unknown</c> is refused, the resolver's own reading is never touched, and clearing
/// falls back to it.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>Maps the account endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/accounts").RequireAuthorization().WithTags("Accounts");
        group.MapPut("/{id:guid}/stage", SetStageOverrideAsync)
            .WithSummary("Set a manual stage override on an account.");
        group.MapDelete("/{id:guid}/stage", ClearStageOverrideAsync)
            .WithSummary("Clear the manual stage override on an account.");
        return endpoints;
    }

    internal static async Task<IResult> SetStageOverrideAsync(
        Guid id,
        SetStageOverrideRequest request,
        TradingCopilotDbContext database,
        HostTradingEnvironment environment,
        CancellationToken cancellationToken)
    {
        // Whitelist, not blacklist (the fail-open lesson): only defined, declarable stages pass. Unknown is not
        // a declaration -- clearing the override is how the operator says "I don't know".
        if (request.Stage == AccountStage.Unknown || !Enum.IsDefined(request.Stage))
        {
            return Results.BadRequest(new
            {
                error = $"'{request.Stage}' is not a declarable stage. Declare Practice, Evaluation, or Funded — or DELETE the override to fall back to the resolved stage.",
            });
        }

        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null)
        {
            return Results.NotFound();
        }

        account.StageOverride = request.Stage;
        return await RecomputeModeAndRespondAsync(account, database, environment, cancellationToken);
    }

    internal static async Task<IResult> ClearStageOverrideAsync(
        Guid id,
        TradingCopilotDbContext database,
        HostTradingEnvironment environment,
        CancellationToken cancellationToken)
    {
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null)
        {
            return Results.NotFound();
        }

        account.StageOverride = null;
        return await RecomputeModeAndRespondAsync(account, database, environment, cancellationToken);
    }

    /// <summary>
    /// Write point 2 of 3 for the persisted mode (gh#7): the override just moved the effective stage, so the
    /// mode is recomputed under the owning firm's conventions before the save.
    /// </summary>
    private static async Task<IResult> RecomputeModeAndRespondAsync(
        Account account,
        TradingCopilotDbContext database,
        HostTradingEnvironment environment,
        CancellationToken cancellationToken)
    {
        FirmConventions conventions = await database.ConventionsForConnectionAsync(account.ConnectionId, cancellationToken);
        account.RecomputeMode(conventions);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(AccountResponse.From(account, environment));
    }
}
