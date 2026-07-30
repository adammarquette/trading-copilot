using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// The suggestion read endpoints (gh#540, R-4): <c>GET /accounts/{id}/suggestions</c> and
/// <c>GET /suggestions/{id}</c> — the first way an operator can read back a suggestion.
/// </summary>
/// <remarks>
/// <para>
/// The agent-review route (gh#402) has been writing <see cref="Suggestion"/> rows since it shipped, and until now the
/// only production reader was a <c>CountAsync</c> in the recovery rehydrator. This is a <b>read model only</b>: it
/// proposes nothing, takes nothing, and touches no order, gate or venue type.
/// </para>
/// <para>
/// <b>Tenancy is the DbContext's, not this file's.</b> Both handlers are ordinary request paths, so the automatic
/// <c>IUserOwned</c> default-deny filter applies and neither may call <c>IgnoreQueryFilters</c> — that is reserved
/// for background plumbing with no request user. A stranger therefore gets an empty list and a <b>404</b> by id, so
/// a row's existence is never disclosed.
/// </para>
/// </remarks>
public static class SuggestionEndpoints
{
    /// <summary>Maps the suggestion read endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSuggestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Mirrors the shipped order routes: a per-account collection plus a by-id resource.
        endpoints.MapGroup("/accounts/{accountId:guid}/suggestions").RequireAuthorization().MapGet("/", ListAsync);
        endpoints.MapGroup("/suggestions/{id:guid}").RequireAuthorization().MapGet("/", GetAsync);
        return endpoints;
    }

    /// <summary>
    /// Lists an account's suggestions, newest first. Defaults to the <b>actionable</b> set
    /// (<see cref="SuggestionState.Active"/>) — a decision surface should not open on rows that can no longer be
    /// acted on; the rest stay reachable by explicit filter and by id.
    /// </summary>
    /// <param name="accountId">The account whose suggestions to list.</param>
    /// <param name="state">The lifecycle state to filter to; omitted means active only.</param>
    /// <param name="limit">The page size; clamped to the configured maximum.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="options">The read-model limits.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The page of suggestions.</returns>
    internal static async Task<IResult> ListAsync(
        Guid accountId,
        SuggestionState? state,
        int? limit,
        TradingCopilotDbContext database,
        IOptions<SuggestionReadOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        SuggestionReadOptions config = options.Value;
        if (limit is <= 0)
        {
            return Results.BadRequest(new { error = "Limit must be positive." });
        }

        // The unset zero is not a state -- refuse it rather than silently returning nothing.
        if (state is SuggestionState.Unknown)
        {
            return Results.BadRequest(new { error = "State must be Active, Stale or ExpiredVoid." });
        }

        int take = Math.Clamp(limit ?? config.DefaultPageSize, 1, config.MaxPageSize);
        SuggestionState wanted = state ?? SuggestionState.Active;

        List<Suggestion> rows = await database.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.AccountId == accountId && suggestion.State == wanted)
            .OrderByDescending(suggestion => suggestion.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SuggestionListResponse([.. rows.Select(SuggestionResponse.From)]));
    }

    /// <summary>
    /// Reads one suggestion by id, in <b>any</b> state — an expired or superseded row stays readable, because the
    /// journal outlives the decision window.
    /// </summary>
    /// <param name="id">The suggestion's id.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The suggestion, or 404 when it does not exist or belongs to another operator.</returns>
    internal static async Task<IResult> GetAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        Suggestion? suggestion = await database.Suggestions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return suggestion is null
            ? Results.NotFound()
            : Results.Ok(SuggestionResponse.From(suggestion));
    }
}
