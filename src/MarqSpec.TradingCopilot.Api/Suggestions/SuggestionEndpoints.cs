using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
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
        endpoints.MapGroup("/accounts/{accountId:guid}/suggestions").RequireAuthorization().WithTags("Suggestions")
            .MapGet("/", ListAsync).WithSummary("List an account's suggestions.");

        RouteGroupBuilder byId = endpoints.MapGroup("/suggestions/{id:guid}").RequireAuthorization().WithTags("Suggestions");
        byId.MapGet("/", GetAsync).WithSummary("Get a suggestion by id.");
        byId.MapPost("/pass", PassAsync)
            .WithSummary("Record a disposition (taken / modified / passed) on a suggestion.");
        byId.MapPost("/take", (Guid id, SuggestionTakeRequest? request, ICurrentUser currentUser, TradingCopilotDbContext database,
            IInstrumentSpecSource instrumentSpecs, IOptions<SuggestionOptions> options, IProjectXVenueFactory venueFactory,
            IOptions<ProjectXConnectionOptions> projectXOptions, IOptions<ExecutionOptions> executionOptions,
            HostTradingEnvironment environment, IKillSwitch killSwitch, IExecutionMetrics metrics,
            IAccountEntryGuard entryGuard, CancellationToken cancellationToken) =>
            TakeAsync(id, request, DateTimeOffset.UtcNow, currentUser, database, instrumentSpecs, options, venueFactory,
                projectXOptions, executionOptions, environment, killSwitch, metrics, entryGuard, cancellationToken))
            .WithSummary("Arm an editable, unsent order ticket from a suggestion.");
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

        // Head of the supersede chain, by default (gh#550): a superseded incumbent is voided to ExpiredVoid, so the
        // Active default already excludes it and surfaces only the actionable head. Superseded rows stay reachable by
        // id and by an explicit ExpiredVoid filter — the journal keeps the whole chain.
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

        if (suggestion is null)
        {
            return Results.NotFound();
        }

        // Surface the operator's disposition and its deviations on the get-by-id read (gh#549, R-8): at most one exists
        // (the one-per-suggestion rule), and R-20 auto-scopes it to the caller. The list read omits it deliberately.
        SuggestionDisposition? disposition = await database.SuggestionDispositions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.SuggestionId == id, cancellationToken);

        return Results.Ok(Project(suggestion, instrumentSpecs, disposition));
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

    /// <summary>
    /// Arms an order ticket from a suggestion (gh#548, R-4 / R-11b / R-12, ADR-0007): <c>POST /suggestions/{id}/take</c>.
    /// The <b>first</b> path that turns a suggestion into an order — so it is where every "still takeable?" refusal
    /// lives, and the one place <see cref="Order.SuggestionId"/> is populated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arm, not send.</b> This stages an editable <see cref="OrderStatus.Staged"/> ticket through the shared
    /// composition ladder <b>minus transmission</b> and touches no venue order path; sending stays the existing,
    /// fully gated order endpoint (R-11b). The risk gate is <b>untouched and still authoritative</b> — it resizes or
    /// blocks exactly as it does for a manual arm.
    /// </para>
    /// <para>
    /// <b>The suggestion-side half of R-12.</b> A take is refused unless the row is takeable <b>right now</b>:
    /// <see cref="SuggestionState.Active"/>, inside its validity window <b>re-read against the clock here</b> (via the
    /// same <see cref="SuggestionLifecycle.Decide"/> the sweep uses — the expiry sweep and the drift consumer are both
    /// eventually consistent), un-dispositioned, and with the market still inside the entry tolerance re-measured now.
    /// The tick size, point value and safety-stop distance are resolved from the <b>server-side</b> spec source and a
    /// miss <b>fails closed</b>; the catastrophic safety stop is derived from entry and that distance, its ordering
    /// pre-checked so <c>CK_StopPlans_SafetyBeyondActual</c> can never trip; and the size is the suggestion's, never
    /// re-derived.
    /// </para>
    /// </remarks>
    /// <param name="id">The suggestion to arm.</param>
    /// <param name="request">The take body — carries only the current market reference.</param>
    /// <param name="now">The clock, injected so the validity re-check is testable.</param>
    /// <param name="currentUser">The request's operator; the arm's writers stamp it.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="instrumentSpecs">The server-side contract-spec source (gh#541); a miss fails closed.</param>
    /// <param name="options">The suggestion limits, for the drift tolerance band.</param>
    /// <param name="venueFactory">The venue factory for the shared composition ladder.</param>
    /// <param name="projectXOptions">The process credential-key guard input (ADR-0015).</param>
    /// <param name="executionOptions">The sanity-cap source for the gate.</param>
    /// <param name="environment">The host trading environment.</param>
    /// <param name="killSwitch">The kill switch the ladder honours.</param>
    /// <param name="metrics">The execution metrics sink.</param>
    /// <param name="entryGuard">The per-account entry lock (gh#531) that serializes the stage against a racing take.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The staged ticket with its gate decision, or the refusal that explains why it was not armed.</returns>
    internal static async Task<IResult> TakeAsync(
        Guid id,
        SuggestionTakeRequest? request,
        DateTimeOffset now,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IInstrumentSpecSource instrumentSpecs,
        IOptions<SuggestionOptions> options,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<ExecutionOptions> executionOptions,
        HostTradingEnvironment environment,
        IKillSwitch killSwitch,
        IExecutionMetrics metrics,
        IAccountEntryGuard entryGuard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(instrumentSpecs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entryGuard);

        // A missing or non-positive reference is not a price. Refuse with a clear 400 rather than letting a zero read
        // as a huge drift -- the reference is caller-supplied on every order path, but it must actually be one.
        if (request is null || request.ReferencePrice <= 0m)
        {
            return Results.BadRequest(new { error = "A positive reference price is required to take a suggestion." });
        }

        // R-20: the DbContext filter makes another operator's suggestion a 404, never a disclosure.
        Suggestion? suggestion = await database.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (suggestion is null)
        {
            return Results.NotFound();
        }

        // The suggestion-side half of R-12: refuse unless takeable NOW. Decide re-reads the clock (gh#545) rather than
        // trusting stored State, so a suggestion the sweep has not yet voided is caught the moment its window passes.
        SuggestionState effective = SuggestionLifecycle.Decide(suggestion.State, suggestion.ExpiresAt, now);
        if (effective != SuggestionState.Active)
        {
            string reason = effective == SuggestionState.ExpiredVoid && suggestion.State != SuggestionState.ExpiredVoid
                ? "This suggestion's validity window has passed and it can no longer be taken."
                : $"This suggestion is {suggestion.State} and can no longer be taken.";
            return Results.UnprocessableEntity(new { error = reason });
        }

        // One disposition per suggestion: an operator who already passed (or took) it does not get a second path.
        bool alreadyDisposed = await database.SuggestionDispositions
            .AnyAsync(existing => existing.SuggestionId == id, cancellationToken);
        if (alreadyDisposed)
        {
            return Results.Conflict(new { error = "This suggestion already has a disposition and cannot be taken." });
        }

        // Anti-stacking (defensive; from the gh#548 adversarial review). Part A writes no disposition, so the check
        // above cannot yet stop a double-click on Approve from minting two Staged tickets off one suggestion -- which
        // the send-vs-take gap (gh#589) could then transmit as double the intended risk. Refuse while a NON-TERMINAL
        // order from this suggestion is already live; a cancelled / rejected / filled one frees it, and the
        // taken-forever semantic is the part-B disposition's (gh#549), not this guard's.
        //
        // THIS READ IS A COURTESY REFUSAL, NOT THE GUARD (PR #615 review). It is unsynchronized, so two concurrent
        // takes can both answer "no" here before either has committed. It stays because it keeps the refusal ladder's
        // order -- an already-armed suggestion gets its 409 ahead of a spec/drift/ordering refusal -- and returns the
        // common case without paying for a lock. The AUTHORITATIVE re-check runs under the per-account lock below,
        // immediately before the insert; that one is what actually closes the race.
        if (await HasLiveOrderAsync(database, id, cancellationToken))
        {
            return LiveOrderConflict();
        }

        // The three numbers a server-originated proposal cannot invent (gh#541). A miss FAILS CLOSED -- never a
        // client-supplied or defaulted tick size, which would silently mis-size every downstream risk calculation.
        if (!InstrumentId.TryParse(suggestion.Instrument, out InstrumentId instrument)
            || !instrumentSpecs.TryResolve(instrument, out InstrumentContractSpec? spec))
        {
            return Results.UnprocessableEntity(new
            {
                error = $"No contract spec is configured for '{suggestion.Instrument}', so it cannot be sized or taken.",
            });
        }

        // THE synchronous drift re-check (R-12): re-measure the current reference against entry ± the tolerance band
        // NOW, so a drifted-but-still-Active suggestion is refused inside the drift consumer's (gh#546) lag window.
        decimal driftTolerance = options.Value.DriftToleranceTicks * spec.Spec.TickSize;
        if (Math.Abs(request.ReferencePrice - suggestion.EntryPrice) > driftTolerance)
        {
            return Results.UnprocessableEntity(new
            {
                error = "The market has drifted past this suggestion's entry tolerance; it can no longer be taken at that level.",
            });
        }

        // The catastrophic safety stop is DERIVED from entry and the spec's distance (ADR-0007), beyond the working
        // stop -- never a client number.
        decimal safetyStop = suggestion.Side == OrderSide.Buy
            ? suggestion.EntryPrice - spec.SafetyStopDistance
            : suggestion.EntryPrice + spec.SafetyStopDistance;

        // Guard CK_StopPlans_SafetyBeyondActual IN CODE: the gate checks stops-below-entry but NOT safety-beyond-working
        // (gh#259), and an arm writes no StopPlan row, so the DB constraint never sees it here. The working stop must
        // sit at or inside the safety floor (coincident is allowed -- it just stages no hidden stop, mirroring
        // AddStopPlan's strict test), or the native bracket would fire tighter than the operator's stop.
        bool ordered = suggestion.Side switch
        {
            OrderSide.Buy => safetyStop <= suggestion.StopPrice && suggestion.StopPrice < suggestion.EntryPrice,
            OrderSide.Sell => safetyStop >= suggestion.StopPrice && suggestion.StopPrice > suggestion.EntryPrice,
            _ => false,
        };
        if (!ordered)
        {
            return Results.UnprocessableEntity(new
            {
                error = "This suggestion's stop is not between its entry and the instrument's catastrophic safety floor, "
                    + "so it cannot be armed safely.",
            });
        }

        // From here it is a manual arm (gh#11, ADR-0007): the SAME ladder, so the risk gate is untouched and still
        // authoritative. Size is the suggestion's, never re-derived; the reference is the caller's; the spec is the
        // server's. A market ticket -- the operator edits type/prices on the staged ticket before sending.
        SendOrderRequest armRequest = new(
            suggestion.Instrument, spec.Spec.TickSize, spec.Spec.PointValue, suggestion.Side, suggestion.Size,
            suggestion.EntryPrice, suggestion.StopPrice, safetyStop, request.ReferencePrice, OrderType.Market,
            suggestion.TargetPrice);

        (OrderEndpoints.Composition? composed, IResult? refusal) = await OrderEndpoints.ComposeAsync(
            suggestion.AccountId, database, venueFactory, projectXOptions, executionOptions, environment, killSwitch,
            cancellationToken, metrics);
        if (composed is null)
        {
            return refusal!;
        }

        (ExecutionRequest? executionRequest, IResult? proposalRefusal) = await OrderEndpoints.BuildRequestAsync(
            composed, armRequest.Symbol, armRequest.TickSize, armRequest.PointValue, armRequest.Side, armRequest.Quantity,
            armRequest.Entry, armRequest.Stop, armRequest.SafetyStop, armRequest.ReferencePrice, armRequest.Target,
            armRequest.Type, cancellationToken);
        if (executionRequest is null)
        {
            return proposalRefusal!;
        }

        ExecutionResult result = composed.Execution.Evaluate(executionRequest);
        if (result.Outcome != ExecutionOutcome.Evaluated || result.Decision is null)
        {
            return Results.Conflict(new { error = result.Reason }); // pre-gate refusal: nothing staged, nothing sized
        }

        // Serialize the stage per account (gh#531's seam, reused here for take-vs-take -- PR #615 review). Two
        // near-simultaneous takes of one suggestion -- a double-click on Approve, or a client retry -- each get their
        // own scope and their own DbContext, so nothing in process arbitrates them: both read the unsynchronized
        // check above as false before either has saved, and both insert a Staged ticket against the same suggestion.
        // That is exactly the double-ticket the check exists to prevent, and via the send-vs-take gap (gh#589) up to
        // twice the intended risk. gh#531 closed the send-vs-send shape this way and explicitly deferred send-vs-take;
        // take-vs-take is neither, and is closed here.
        //
        // The re-check runs INSIDE the callback deliberately, exactly as TransmitAsync's does: only there is it under
        // the lock, so two racers cannot both answer "no" before either has journaled. Composition and the gate stay
        // OUTSIDE -- staging transmits nothing, and the send path re-composes and re-gates under this same lock.
        return await entryGuard.RunExclusiveAsync(database, suggestion.AccountId, async () =>
        {
            // THE guard. The first racer to get here journals its Staged row; the second, serialized behind it, sees
            // that row and refuses.
            if (await HasLiveOrderAsync(database, id, cancellationToken))
            {
                return LiveOrderConflict();
            }

            // Stage WHATEVER the gate said (a blocked/resized proposal is what the operator edits, ADR-0007), carrying
            // the suggestion's size, and stamp the originating suggestion so "taken" is traceable end to end (gh#548).
            Order staged = OrderEndpoints.NewOrderRow(
                currentUser, composed.Account, armRequest, executionRequest.Contract.Contract.Key,
                OrderStatus.Staged, suggestion.Size, OrderEntryMethod.ArmedTake);
            staged.SuggestionId = suggestion.Id;
            database.Orders.Add(staged);
            OrderEndpoints.PersistDecision(database, currentUser, composed.Account.Id, staged.Id, result.Decision);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(StagedOrderResponse.From(staged, result.Decision));
        }, cancellationToken);
    }

    // A NON-TERMINAL order from this suggestion means it is already armed. Cancelled / Rejected / Filled free it;
    // the taken-forever semantic belongs to the part-B disposition (gh#549), not to this check.
    private static Task<bool> HasLiveOrderAsync(
        TradingCopilotDbContext database, Guid suggestionId, CancellationToken cancellationToken) =>
        database.Orders.AnyAsync(
            existing => existing.SuggestionId == suggestionId
                && (existing.Status == OrderStatus.Staged || existing.Status == OrderStatus.Taking
                    || existing.Status == OrderStatus.Working || existing.Status == OrderStatus.PartiallyFilled),
            cancellationToken);

    private static IResult LiveOrderConflict() => Results.Conflict(new
    {
        error = "This suggestion already has a live order; cancel or complete it before taking again.",
    });

    // A blank note is no note: trim, and collapse empty/whitespace to null so "" and "   " are not stored as a note.
    private static string? Normalize(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    // Money-values the geometry where the instrument has a configured spec (gh#541). An unparseable or unconfigured
    // symbol simply omits the dollar figures -- a display concern degrades, it does not fail the read.
    private static SuggestionResponse Project(
        Suggestion suggestion, IInstrumentSpecSource instrumentSpecs, SuggestionDisposition? disposition = null)
    {
        InstrumentContractSpec? spec = null;
        if (InstrumentId.TryParse(suggestion.Instrument, out InstrumentId instrument))
        {
            instrumentSpecs.TryResolve(instrument, out spec);
        }

        return SuggestionResponse.From(suggestion, spec, disposition);
    }
}
