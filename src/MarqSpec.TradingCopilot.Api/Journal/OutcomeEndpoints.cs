using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Journal;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>
/// The journal-outcome read + R-15 removal surface (gh#909, R-9 / R-15): list the operator's outcomes with the
/// inclusive-vs-exclusive-of-soft-deleted report toggle, and move the three <b>independent</b> R-15 removal flags —
/// soft-delete / restore, training exclusion, and default-view visibility.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenancy is the DbContext's.</b> Every handler is an ordinary request path, so the automatic <c>IUserOwned</c>
/// default-deny filter (R-20 / ADR-0017) applies and none calls <c>IgnoreQueryFilters</c> — a stranger's outcome is a
/// <b>404</b>, never disclosed. The soft removals go through <see cref="Outcome"/>'s own methods (its flag setters are
/// private), so the R-15 invariant that soft-delete moves all three flags together — while the independent toggles move
/// one at a time — cannot be bypassed by a direct column write.
/// </para>
/// <para>
/// <b>Removals span the reversible controls and the one irreversible one.</b> Soft-delete / restore and the two
/// toggles are undoable; <b>hard delete</b> is the confirmed exception — it removes the row, but records the <b>fact</b>
/// of the deletion to the audit trail (R-15), so the removal outlives the content. Nothing here touches a broker or
/// account record.
/// </para>
/// </remarks>
public static class OutcomeEndpoints
{
    /// <summary>Maps the outcome read + removal endpoints under <c>/outcomes</c>. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOutcomeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/outcomes").RequireAuthorization().WithTags("Journal");

        group.MapGet("/", ListAsync)
            .WithSummary("List the operator's journal outcomes; ?includeDeleted toggles soft-deleted rows in or out.");
        group.MapPost("/{id:guid}/soft-delete", SoftDeleteAsync)
            .WithSummary("Soft-delete an outcome (R-15) — hides it and excludes it from training, together and reversibly.");
        group.MapPost("/{id:guid}/restore", RestoreAsync)
            .WithSummary("Restore a soft-deleted outcome (R-15) — back into the default view and the learning set.");
        group.MapPut("/{id:guid}/training-exclusion", SetTrainingExclusionAsync)
            .WithSummary("Include or exclude an outcome from the AI learning set (R-15), independently of its visibility.");
        group.MapPut("/{id:guid}/visibility", SetVisibilityAsync)
            .WithSummary("Show or hide an outcome in default journal views (R-15), independently of whether it trains.");
        // now is injected (not bound) so the audit timestamp is testable, mirroring the chat / disposition writers.
        group.MapDelete("/{id:guid}", (Guid id, TradingCopilotDbContext database, IAuditLog auditLog,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            HardDeleteAsync(id, DateTimeOffset.UtcNow, database, auditLog, loggerFactory, cancellationToken))
            .WithSummary("Hard-delete an outcome (R-15) — the confirmed, irreversible removal; the deletion fact is audited.");

        return endpoints;
    }

    /// <summary>
    /// Lists the operator's outcomes (<c>GET /outcomes</c>), R-20 auto-scoped. <paramref name="includeDeleted"/>
    /// toggles soft-deleted rows in or out — the R-15 report toggle, so the same period reads with and without them.
    /// </summary>
    /// <param name="includeDeleted">Whether to include soft-deleted rows; defaults to excluding them.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The operator's outcomes.</returns>
    internal static async Task<IResult> ListAsync(
        bool? includeDeleted,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        List<Outcome> rows = await database.Outcomes
            .AsNoTracking()
            .IncludingDeleted(includeDeleted ?? false)
            .ToListAsync(cancellationToken);

        return Results.Ok(new OutcomeListResponse([.. rows.Select(OutcomeResponse.From)]));
    }

    /// <summary>Soft-deletes an outcome (<c>POST /outcomes/{id}/soft-delete</c>), or 404 when foreign/absent (R-20).</summary>
    internal static Task<IResult> SoftDeleteAsync(
        Guid id, TradingCopilotDbContext database, CancellationToken cancellationToken) =>
        MutateAsync(id, outcome => outcome.SoftDelete(), database, cancellationToken);

    /// <summary>Restores a soft-deleted outcome (<c>POST /outcomes/{id}/restore</c>), or 404 (R-20).</summary>
    internal static Task<IResult> RestoreAsync(
        Guid id, TradingCopilotDbContext database, CancellationToken cancellationToken) =>
        MutateAsync(id, outcome => outcome.Restore(), database, cancellationToken);

    /// <summary>Sets an outcome's training-exclusion flag (<c>PUT /outcomes/{id}/training-exclusion</c>), or 404 (R-20).</summary>
    internal static Task<IResult> SetTrainingExclusionAsync(
        Guid id,
        OutcomeFlagRequest? request,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(Results.BadRequest(new { error = "A boolean 'value' is required." }));
        }

        return MutateAsync(id, outcome => outcome.SetTrainingExcluded(request.Value), database, cancellationToken);
    }

    /// <summary>Sets an outcome's default-view visibility (<c>PUT /outcomes/{id}/visibility</c>), or 404 (R-20).</summary>
    internal static Task<IResult> SetVisibilityAsync(
        Guid id,
        OutcomeFlagRequest? request,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(Results.BadRequest(new { error = "A boolean 'value' is required." }));
        }

        // 'value' true means HIDDEN — the request models the flag, and SetHiddenFromUser takes hidden.
        return MutateAsync(id, outcome => outcome.SetHiddenFromUser(request.Value), database, cancellationToken);
    }

    /// <summary>
    /// Hard-deletes an outcome (<c>DELETE /outcomes/{id}</c>, R-15) — the confirmed, irreversible removal — then records
    /// the <b>fact</b> of the deletion to the audit trail, or a <b>404</b> when the outcome is foreign or absent (R-20).
    /// </summary>
    /// <param name="id">The outcome to remove.</param>
    /// <param name="now">The clock, injected so the audit timestamp is testable.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="auditLog">The append-only audit trail — records that the deletion occurred (R-15).</param>
    /// <param name="loggerFactory">The logger factory (a failed audit write is logged, never surfaced).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>204 when removed, or 404.</returns>
    internal static async Task<IResult> HardDeleteAsync(
        Guid id,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        IAuditLog auditLog,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        Outcome? outcome = await database.Outcomes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (outcome is null)
        {
            return Results.NotFound(); // not found / not owned (R-20) — never a disclosure
        }

        // Capture what the audit names BEFORE the row is gone.
        Guid owner = outcome.UserId;
        OutcomeResolution resolution = outcome.Resolution;

        database.Outcomes.Remove(outcome);
        await database.SaveChangesAsync(cancellationToken);

        // R-15: the content is gone, but the FACT of the deletion is still logged. A SECONDARY write, the IAuditLog
        // posture — the removal is the operator's confirmed action and has committed, so the audit is written AFTER
        // and its failure is logged, never surfaced: a transient audit fault must not resurrect a row the operator
        // deliberately removed, and recording a deletion that did not happen would be worse than a logged gap. The
        // delete just proved the database reachable, so the audit all but always succeeds.
        try
        {
            await auditLog.WriteAsync(
                [
                    new AuditRecord
                    {
                        Id = Guid.NewGuid(),
                        UserId = owner, // the outcome's owner (R-20)
                        Action = AuditAction.OutcomeHardDeleted,
                        Placement = AuditPlacement.None, // concerns no protective leg
                        Source = null, // not a kill / flatten action (CK_AuditRecords_Source_MatchesAction)
                        SyntheticRisk = false,
                        Detail = $"Hard-deleted journal outcome {id} (was {resolution}); R-15 confirmed removal.",
                        RecordedAt = now,
                    },
                ],
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            loggerFactory.CreateLogger("MarqSpec.TradingCopilot.Api.Journal.OutcomeHardDelete")
                .LogError(error, "Hard-deleted outcome {Outcome} but could not write its R-15 deletion audit.", id);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Loads a tracked, R-20-scoped outcome, applies <paramref name="mutate"/> (one of <see cref="Outcome"/>'s own
    /// R-15 methods), saves, and returns the updated row — or a 404 when the outcome is foreign or absent.
    /// </summary>
    private static async Task<IResult> MutateAsync(
        Guid id,
        Action<Outcome> mutate,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        Outcome? outcome = await database.Outcomes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (outcome is null)
        {
            return Results.NotFound(); // not found / not owned (R-20) — never a disclosure
        }

        mutate(outcome);
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(OutcomeResponse.From(outcome));
    }
}

/// <summary>A boolean flag request for the R-15 independent toggles (gh#909): <c>true</c> excludes / hides.</summary>
/// <param name="Value">The flag's new value.</param>
public sealed record OutcomeFlagRequest(bool Value);

/// <summary>One journal outcome for the read surface (gh#909, R-9 / R-15), with its removal flags.</summary>
/// <param name="Id">The outcome's id.</param>
/// <param name="TradeId">The trade it resolves, when one was taken.</param>
/// <param name="SuggestionId">The suggestion it scores, when there was one.</param>
/// <param name="Resolution">How it resolved, as a name (<c>Win</c> / <c>Loss</c> / <c>NoFillScratch</c> / <c>Expired</c>).</param>
/// <param name="Simulated">Whether it is a simulation of an untaken suggestion rather than a taken trade.</param>
/// <param name="PredictedRewardRisk">The calibration pair's predicted half, when populated.</param>
/// <param name="RealizedRewardRisk">The calibration pair's realized half, when populated.</param>
/// <param name="TrainingExcluded">Excluded from the AI learning set (R-15).</param>
/// <param name="HiddenFromUser">Hidden from default journal / report views (R-15).</param>
/// <param name="Deleted">Soft-deleted (R-15).</param>
public sealed record OutcomeResponse(
    Guid Id,
    Guid? TradeId,
    Guid? SuggestionId,
    string Resolution,
    bool Simulated,
    decimal? PredictedRewardRisk,
    decimal? RealizedRewardRisk,
    bool TrainingExcluded,
    bool HiddenFromUser,
    bool Deleted)
{
    /// <summary>Projects an <see cref="Outcome"/> to its response, resolution as a name (never a bare integer).</summary>
    /// <param name="outcome">The outcome to project.</param>
    /// <returns>The response.</returns>
    public static OutcomeResponse From(Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new OutcomeResponse(
            outcome.Id,
            outcome.TradeId,
            outcome.SuggestionId,
            outcome.Resolution.ToString(),
            outcome.Simulated,
            outcome.PredictedRewardRisk,
            outcome.RealizedRewardRisk,
            outcome.TrainingExcluded,
            outcome.HiddenFromUser,
            outcome.Deleted);
    }
}

/// <summary>A page of journal outcomes (gh#909).</summary>
/// <param name="Outcomes">The operator's outcomes.</param>
public sealed record OutcomeListResponse(IReadOnlyList<OutcomeResponse> Outcomes);
