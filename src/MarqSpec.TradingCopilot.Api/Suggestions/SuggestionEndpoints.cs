using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Suggestions;
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

        RouteGroupBuilder byId = endpoints.MapGroup("/suggestions/{id:guid}").RequireAuthorization();
        byId.MapGet("/", GetAsync);
        byId.MapPost("/pass", PassAsync);
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
    /// <param name="instrumentSpecs">The contract-spec source used to money-value each suggestion's geometry (gh#541).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The page of suggestions.</returns>
    internal static async Task<IResult> ListAsync(
        Guid accountId,
        SuggestionState? state,
        int? limit,
        TradingCopilotDbContext database,
        IOptions<SuggestionOptions> options,
        IInstrumentSpecSource instrumentSpecs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(instrumentSpecs);

        SuggestionOptions config = options.Value;
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

        IQueryable<Suggestion> query = database.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.AccountId == accountId && suggestion.State == wanted);

        // The default actionable surface excludes suggestions the operator has already dispositioned (gh#547): a
        // passed setup is no longer actionable. It stays reachable by id and by an EXPLICIT state filter — the
        // journal keeps everything; only the default decision surface hides what has been acted on. (A disposition
        // does not move State — gh#539 — so without this a passed-but-still-Active row would sit in the list.)
        if (state is null)
        {
            query = query.Where(suggestion =>
                !database.SuggestionDispositions.Any(disposition => disposition.SuggestionId == suggestion.Id));
        }

        List<Suggestion> rows = await query
            .OrderByDescending(suggestion => suggestion.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SuggestionListResponse([.. rows.Select(row => Project(row, instrumentSpecs))]));
    }

    /// <summary>
    /// Reads one suggestion by id, in <b>any</b> state — an expired or superseded row stays readable, because the
    /// journal outlives the decision window.
    /// </summary>
    /// <param name="id">The suggestion's id.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="instrumentSpecs">The contract-spec source used to money-value the geometry (gh#541).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The suggestion, or 404 when it does not exist or belongs to another operator.</returns>
    internal static async Task<IResult> GetAsync(
        Guid id,
        TradingCopilotDbContext database,
        IInstrumentSpecSource instrumentSpecs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(instrumentSpecs);

        Suggestion? suggestion = await database.Suggestions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return suggestion is null
            ? Results.NotFound()
            : Results.Ok(Project(suggestion, instrumentSpecs));
    }

    // Every defined pass reason OR'd together — the mask an incoming [Flags] value must fit inside.
    private const SuggestionPassReason AllReasons =
        SuggestionPassReason.AlreadyPositioned | SuggestionPassReason.NewsRisk | SuggestionPassReason.WrongTime
        | SuggestionPassReason.WaitingBetterLevel | SuggestionPassReason.WeakRewardRisk | SuggestionPassReason.Sizing
        | SuggestionPassReason.AgainstARule | SuggestionPassReason.LowConviction;

    /// <summary>
    /// Records a <b>neutral pass</b> on a suggestion (gh#547, R-4/R-8): <c>POST /suggestions/{id}/pass</c>. A pass
    /// touches no order, gate or venue — it writes one <see cref="SuggestionDisposition"/> so the R-9 learning loop
    /// has the operator's decline to read.
    /// </summary>
    /// <remarks>
    /// A pass on an <b>already stale or expired</b> suggestion is still accepted — the operator's note is worth
    /// keeping, and lifecycle state is the clock's, not the disposition's (gh#539). One disposition per suggestion:
    /// a second <b>conflicts</b> (409) rather than overwriting, because the journal records the decision, not the
    /// latest edit.
    /// </remarks>
    /// <param name="id">The suggestion to pass on.</param>
    /// <param name="request">The optional reasons and note; a pass with neither is valid.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The recorded disposition, 404 when the suggestion is not the caller's, or 409 when already disposed.</returns>
    internal static async Task<IResult> PassAsync(
        Guid id,
        SuggestionPassRequest? request,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // R-20: the filter makes another operator's suggestion a 404, never a disclosure. No state gate — a pass is
        // accepted on any lifecycle state (active / stale / expired-void).
        Suggestion? suggestion = await database.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (suggestion is null)
        {
            return Results.NotFound();
        }

        SuggestionPassReason reasons = request?.Reasons ?? SuggestionPassReason.None;
        if ((reasons & ~AllReasons) != 0)
        {
            return Results.BadRequest(new { error = "One or more pass reasons are not recognised." });
        }

        string? note = Normalize(request?.Note);
        if (note is not null && note.Length > SuggestionDisposition.NoteMaxLength)
        {
            return Results.BadRequest(new { error = $"Note must be at most {SuggestionDisposition.NoteMaxLength} characters." });
        }

        // One disposition per suggestion: the pre-check gives a clean 409; the unique index is the DB backstop
        // against a race (proven at the DB tier by the QA suite, gh#552).
        bool alreadyDisposed = await database.SuggestionDispositions
            .AnyAsync(existing => existing.SuggestionId == id, cancellationToken);
        if (alreadyDisposed)
        {
            return Results.Conflict(new { error = "This suggestion already has a disposition." });
        }

        SuggestionDisposition disposition = new()
        {
            Id = Guid.NewGuid(),
            UserId = suggestion.UserId, // the caller's, guaranteed by the R-20 filter on the read above
            SuggestionId = suggestion.Id,
            Kind = SuggestionDispositionKind.Passed,
            Reasons = reasons,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.SuggestionDispositions.Add(disposition);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(SuggestionDispositionResponse.From(disposition));
    }

    // A blank note is no note: trim, and collapse empty/whitespace to null so "" and "   " are not stored as a note.
    private static string? Normalize(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    // Money-values the geometry where the instrument has a configured spec (gh#541). An unparseable or unconfigured
    // symbol simply omits the dollar figures -- a display concern degrades, it does not fail the read.
    private static SuggestionResponse Project(Suggestion suggestion, IInstrumentSpecSource instrumentSpecs)
    {
        InstrumentContractSpec? spec = null;
        if (InstrumentId.TryParse(suggestion.Instrument, out InstrumentId instrument))
        {
            instrumentSpecs.TryResolve(instrument, out spec);
        }

        return SuggestionResponse.From(suggestion, spec);
    }
}
