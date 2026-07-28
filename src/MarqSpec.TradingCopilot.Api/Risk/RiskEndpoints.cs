using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Risk;

/// <summary>
/// The <c>/accounts/{id}/risk</c> endpoints (R-5, gh#10) — the operator declares an account's risk rules; the
/// S3 gate composes from exactly what is persisted. A declaration is validated <b>through the domain
/// factories</b> (<see cref="RiskProfile.Declare"/>, <see cref="TrailingDrawdown.Start"/>,
/// <see cref="ManualCaps.Create"/> — one source of truth) and refused whole on any violation; an account with
/// nothing declared has <i>no</i> profile — the gate's fail-closed input, never a fabricated default.
/// </summary>
public static class RiskEndpoints
{
    /// <summary>Maps the risk endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapRiskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/accounts/{id:guid}/risk").RequireAuthorization();
        group.MapPut("/", DeclareRiskAsync);
        group.MapGet("/", GetRiskAsync);
        return endpoints;
    }

    internal static async Task<IResult> DeclareRiskAsync(
        Guid id,
        DeclareRiskProfileRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null)
        {
            return Results.NotFound();
        }

        // The default-entry-action preference (R-11, gh#218): defaulting to Send as-is is practice-only AND
        // confirm-to-enable, enforced here server-side -- never merely hidden in the UI. Setting ApproveAndArm (the
        // review-first default) is always free. The mode rule lives in TradingModePolicy, the home of mode rules.
        if (request.DefaultEntryAction == DefaultEntryAction.SendAsIs)
        {
            if (!TradingModePolicy.SendAsIsDefaultAllowed(account.Mode))
            {
                return Results.Conflict(new
                {
                    error = $"A {account.Mode} account may not default to Send as-is — it is practice-only (R-11). "
                        + "The send-as-is action stays available and fully gated (R-5 / R-16 / R-12); only defaulting to it is restricted.",
                });
            }

            if (!request.ConfirmSendAsIsDefault)
            {
                return Results.UnprocessableEntity(new
                {
                    error = "Defaulting to Send as-is requires explicit confirmation. Re-send with confirmSendAsIsDefault = true.",
                });
            }
        }

        // Validate the WHOLE declaration through the domain factories before anything is stored -- the
        // FirmConventions.For pattern: one source of truth, and a bad declaration turns into a 400 here rather
        // than a DB constraint error later. The trailing floor is started against the account's balance purely
        // to exercise TrailingDrawdown.Start's validation; the started state is discarded (the floor is runtime
        // state, gh#10-deferred).
        try
        {
            _ = RiskProfile.Declare(
                request.PerTradeRiskFraction,
                request.TargetRewardRatio,
                request.MaxDrawdownPerTrade,
                request.DailyDrawdownGovernor,
                request.DailyProfitTarget,
                request.StopForDayAtProfitTarget,
                request.SizingBasis);
            if (request.StartingBalance <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(request.StartingBalance), request.StartingBalance, "The starting balance must be positive.");
            }

            _ = TrailingDrawdown.Start(request.TrailingMode, request.TrailingAmount, request.StartingBalance, request.LocksAt);
            _ = ManualCaps.Create(request.MaxContractsPerOrder);
        }
        catch (ArgumentOutOfRangeException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }

        // The consistency target has no domain object yet (its evaluator arrives with the risk runtime), so the
        // boundary validates the fraction explicitly -- same range discipline as the DB check.
        if (request.MaxBestDayFraction is <= 0m or > 1m)
        {
            return Results.BadRequest(new { error = "The consistency target must be in (0, 1] when set — null means none." });
        }

        RiskProfileRecord? existing = await database.RiskProfiles
            .FirstOrDefaultAsync(candidate => candidate.AccountId == id, cancellationToken);
        if (existing is null)
        {
            existing = new RiskProfileRecord
            {
                Id = Guid.NewGuid(),
                UserId = currentUser.UserId,
                AccountId = id,
                StartingBalance = request.StartingBalance,
                FloorSource = request.FloorSource,
                TrailingMode = request.TrailingMode,
                TrailingAmount = request.TrailingAmount,
                PerTradeRiskFraction = request.PerTradeRiskFraction,
                TargetRewardRatio = request.TargetRewardRatio,
                MaxDrawdownPerTrade = request.MaxDrawdownPerTrade,
                DailyDrawdownGovernor = request.DailyDrawdownGovernor,
                SizingBasis = request.SizingBasis,
                MaxContractsPerOrder = request.MaxContractsPerOrder,
            };
            database.RiskProfiles.Add(existing);
        }

        // One profile per account: a redeclaration replaces every field (the request is the complete
        // declaration), it never merges.
        existing.DailyLossLimit = request.DailyLossLimit;
        existing.AccountProfitTarget = request.AccountProfitTarget;
        existing.StartingBalance = request.StartingBalance;
        existing.FloorSource = request.FloorSource;
        existing.TrailingMode = request.TrailingMode;
        existing.TrailingAmount = request.TrailingAmount;
        existing.LocksAt = request.LocksAt;
        existing.PerTradeRiskFraction = request.PerTradeRiskFraction;
        existing.TargetRewardRatio = request.TargetRewardRatio;
        existing.MaxDrawdownPerTrade = request.MaxDrawdownPerTrade;
        existing.DailyDrawdownGovernor = request.DailyDrawdownGovernor;
        existing.DailyProfitTarget = request.DailyProfitTarget;
        existing.StopForDayAtProfitTarget = request.StopForDayAtProfitTarget;
        existing.SizingBasis = request.SizingBasis;
        existing.MaxContractsPerOrder = request.MaxContractsPerOrder;
        existing.MaxBestDayFraction = request.MaxBestDayFraction;
        existing.ConsistencyEnforcement = request.ConsistencyEnforcement;
        existing.DefaultEntryAction = request.DefaultEntryAction; // guarded above when SendAsIs

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(RiskProfileResponse.From(existing));
    }

    internal static async Task<IResult> GetRiskAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        RiskProfileRecord? profile = await database.RiskProfiles
            .FirstOrDefaultAsync(candidate => candidate.AccountId == id, cancellationToken);

        // No default is invented for an undeclared account: absence IS the answer (the gate fail-closes on it).
        return profile is null ? Results.NotFound() : Results.Ok(RiskProfileResponse.From(profile));
    }
}
