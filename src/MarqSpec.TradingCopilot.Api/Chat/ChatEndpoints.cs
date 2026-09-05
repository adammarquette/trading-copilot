using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Chat;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Chat;

/// <summary>
/// The co-pilot chat CRUD (gh#18 inc 2, R-6): <c>/conversations</c> — create a thread, list the operator's threads,
/// read one with its messages, and append a message. The read/write surface the operator and, next, the grounded
/// chat-turn orchestrator go through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenancy is the DbContext's, not this file's.</b> Every handler is an ordinary request path, so the automatic
/// <c>IUserOwned</c> default-deny filter (R-20 / ADR-0017) applies and none may call <c>IgnoreQueryFilters</c>. A
/// stranger therefore gets an empty list and a <b>404</b> by id, so a conversation's existence is never disclosed.
/// </para>
/// <para>
/// <b>A message's owner is the conversation's, never request input.</b> <see cref="AppendMessageRequest"/> carries no
/// <c>UserId</c>; <see cref="AppendAsync"/> stamps <c>message.UserId = conversation.UserId</c> from the R-20-scoped
/// load, so a client can never write a message scoped to someone else (mirrors <c>SuggestionEndpoints.PassAsync</c>).
/// </para>
/// </remarks>
public static class ChatEndpoints
{
    /// <summary>Default conversation-list page size when the caller names none.</summary>
    private const int DefaultPageSize = 50;

    /// <summary>The most conversations one list read may return.</summary>
    private const int MaxPageSize = 200;

    /// <summary>
    /// How many retrieved items to ground a chat turn on (gh#995) — a small top-k the reranker sharpens.
    /// It is a budget for the WHOLE turn rather than a per-kind quota (gh#1065): the pipeline recalls every kind
    /// and reranks them together, so the most relevant few win whichever kind they came from, and adding a kind
    /// never grows the prompt.
    /// </summary>
    private const int GroundingTopK = 4;

    /// <summary>Maps the chat CRUD endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/conversations").RequireAuthorization().WithTags("Chat");

        // now is injected (not bound) so the create/append timestamps are testable, mirroring TakeAsync.
        group.MapPost("/", (CreateConversationRequest? request, ICurrentUser currentUser,
            TradingCopilotDbContext database, CancellationToken cancellationToken) =>
            CreateAsync(request, DateTimeOffset.UtcNow, currentUser, database, cancellationToken))
            .WithSummary("Start a conversation.");
        group.MapGet("/", ListAsync).WithSummary("List the operator's conversations, most-recent-first.");
        group.MapGet("/{id:guid}", GetAsync).WithSummary("Get a conversation and its messages, in order.");
        group.MapPost("/{id:guid}/messages", (Guid id, AppendMessageRequest? request,
            TradingCopilotDbContext database, CancellationToken cancellationToken) =>
            AppendAsync(id, request, DateTimeOffset.UtcNow, database, cancellationToken))
            .WithSummary("Append a message to a conversation.");
        group.MapPost("/{id:guid}/turns", (Guid id, ChatTurnRequest? request, TradingCopilotDbContext database,
            IChatTurnService turnService, IContextRetrievalService retrieval, IAiSpendGovernor governor,
            IOptions<GovernorOptions> governorOptions, IAiUsageLedger ledger, ILlmMetrics metrics,
            IChatRealtimeNotifier notifier, IChatTurnGuard turnGuard, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            TurnAsync(id, request, DateTimeOffset.UtcNow, database, turnService, retrieval, governor, governorOptions,
                ledger, metrics, notifier, turnGuard, loggerFactory, cancellationToken))
            .WithSummary("Take a grounded co-pilot chat turn.");

        return endpoints;
    }

    /// <summary>
    /// Creates a conversation (<c>POST /conversations</c>). The owner is stamped from <see cref="ICurrentUser"/> — the
    /// R-20 filter reads it back, but does not stamp inserts, so the writer must set it (the <c>PassAsync</c> pattern).
    /// </summary>
    /// <param name="request">The optional title; a conversation may start untitled.</param>
    /// <param name="now">The clock, injected so the created/updated timestamps are testable.</param>
    /// <param name="currentUser">The request's operator; stamped as the owner.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The created conversation, or 400 when the title exceeds its cap.</returns>
    internal static async Task<IResult> CreateAsync(
        CreateConversationRequest? request,
        DateTimeOffset now,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(database);

        string? title = Normalize(request?.Title);
        if (title is not null && title.Length > Conversation.TitleMaxLength)
        {
            return Results.BadRequest(new { error = $"Title must be at most {Conversation.TitleMaxLength} characters." });
        }

        Conversation conversation = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId, // R-20 reads by this filter but does not stamp inserts; set it explicitly
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };
        database.Conversations.Add(conversation);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(ConversationResponse.From(conversation));
    }

    /// <summary>
    /// Lists the operator's conversations, <b>most-recent-first</b> (<c>UpdatedAt</c> desc), clamped page size
    /// (<c>GET /conversations</c>). R-20 auto-scopes the read, so it is the caller's conversations only.
    /// </summary>
    /// <param name="limit">The page size; clamped to <see cref="MaxPageSize"/>. Non-positive is a 400.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The page of conversations.</returns>
    internal static async Task<IResult> ListAsync(
        int? limit,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (limit is <= 0)
        {
            return Results.BadRequest(new { error = "Limit must be positive." });
        }

        int take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        List<Conversation> rows = await database.Conversations
            .AsNoTracking()
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ConversationListResponse([.. rows.Select(ConversationResponse.From)]));
    }

    /// <summary>
    /// Reads a conversation and its messages in <c>Sequence</c> order (<c>GET /conversations/{id}</c>), or <b>404</b>
    /// when it does not exist or belongs to another operator (the R-20 filter — never a disclosure).
    /// </summary>
    /// <param name="id">The conversation's id.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The conversation with its messages, or 404.</returns>
    internal static async Task<IResult> GetAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        Conversation? conversation = await database.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        // The messages carry their own UserId (R-20 scopes this direct read too), so the join is belt-and-suspenders.
        List<ChatMessage> messages = await database.ChatMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == id)
            .OrderBy(message => message.Sequence)
            .ToListAsync(cancellationToken);

        return Results.Ok(ConversationDetailResponse.From(conversation, messages));
    }

    /// <summary>
    /// Appends a message to a conversation (<c>POST /conversations/{id}/messages</c>). The message's owner is the
    /// <b>conversation's</b> (guard 1); its <c>Sequence</c> is allocated <c>max+1</c> with the unique
    /// <c>(ConversationId, Sequence)</c> index as the backstop (guard 2); the append bumps the conversation's
    /// <c>UpdatedAt</c> so the list read surfaces the freshest thread first.
    /// </summary>
    /// <param name="id">The conversation to append to.</param>
    /// <param name="request">The message role and content; it carries no owner.</param>
    /// <param name="now">The clock, injected so the created/updated timestamps are testable.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The appended message, 404 for a foreign/absent conversation, 400 for a bad role/content, or 409 on a
    /// sequence race.</returns>
    internal static async Task<IResult> AppendAsync(
        Guid id,
        AppendMessageRequest? request,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Validate the request shape FIRST — a 400 here reveals nothing about whether the conversation exists (we have
        // not looked yet), so it cannot become an existence oracle. Role fails closed: Unknown (0) and any undefined
        // value are refused, so only an explicit User / Assistant / System turn is stored.
        if (request is null || !Enum.IsDefined(request.Role) || request.Role == ChatRole.Unknown)
        {
            return Results.BadRequest(new { error = "A message role of User, Assistant or System is required." });
        }
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "Message content is required." });
        }
        if (request.Content.Length > ChatMessage.ContentMaxLength)
        {
            return Results.BadRequest(new { error = $"Message content must be at most {ChatMessage.ContentMaxLength} characters." });
        }

        // R-20: the filter makes a foreign or absent conversation a 404 — and this scoped load is what makes guard 1
        // (the owner is the conversation's) sound: we only ever copy an owner we were allowed to read.
        Conversation? conversation = await database.Conversations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        // GUARDS 1 + 2 (gh#900 review): TryAppendAsync stamps the owner from the loaded conversation — never request
        // input — and allocates Sequence max+1 with the unique (ConversationId, Sequence) index as the backstop; a
        // concurrent append that lost the race is a graceful 409.
        (ChatMessage? appended, bool conflict) = await TryAppendAsync(
            database, conversation, request.Role, request.Content, now, cancellationToken);
        return conflict ? SequenceConflict() : Results.Ok(ChatMessageResponse.From(appended!));
    }

    /// <summary>
    /// Takes a grounded co-pilot chat turn (<c>POST /conversations/{id}/turns</c>, gh#906, R-6): appends the
    /// operator's message, runs the model over the thread, meters and ledgers the call, appends the reply, and pushes
    /// it to the owner's realtime connections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order and safety.</b> A foreign / absent conversation is a <b>404</b> (R-20). The <b>governor gate</b>
    /// (gh#448, ADR-0008) caps deployment-wide daily AI spend <b>before</b> any call — inert until a budget is set,
    /// and <b>fail-open</b> on a spend-read fault (the inverse of the fail-closed risk gate: a bookkeeping blip must
    /// never pause the co-pilot). The operator's turn is persisted <b>before</b> the call, so a later LLM fault never
    /// loses it; the call is metered (export-only) and ledgered (durable, <b>fail-open</b>) whatever its outcome, so
    /// the governor sees every billed turn. A refused / faulted turn is <b>fail-closed</b> — a 422 with the reason,
    /// and <b>no assistant turn is invented</b>. Enforcement lives below the model: nothing here proposes an order.
    /// </para>
    /// <para>
    /// <b>Always-on grounding (gh#995, ADR-0027; cross-kind since gh#1065).</b> After the gate passes and the turn
    /// is persisted, the read-only retrieval pipeline fetches a little context for the operator's message across
    /// <b>every</b> retrievable kind — news, the trader's own suggestions, and their journal entries — handed to
    /// the model as <b>untrusted data</b> (user-role content, never the system prompt). The owner-scoped kinds are
    /// scoped inside the pipeline by the tenant filter (R-20), so a wider grounding set never widens whose data is
    /// read. It is fully <b>fail-open</b> — any fault degrades to an un-grounded (history-only) turn — and is
    /// <b>skipped</b> once spend crosses the pre-alert threshold, shedding its extra embed + rerank before the hard
    /// cap would 429 the chat call. It still costs ONE embed and ONE rerank however many kinds are searched. No
    /// second governor gate runs; a 429-blocked turn never reaches retrieval.
    /// </para>
    /// <para>
    /// <b>Presentation-only push.</b> On success the reply is pushed per-owner over the hub (ADR-0021) <b>after</b> the
    /// write commits, and that push's failure never fails the turn — the REST response already carries the answer to
    /// the initiating caller. Token streaming (3b) extends the provider seam and is deferred (gh#906).
    /// </para>
    /// <para>
    /// <b>One in-flight turn per conversation</b> (gh#1106). The whole turn runs inside
    /// <see cref="IChatTurnGuard"/>'s per-conversation lock, and a second concurrent turn is refused with a
    /// <b>409</b> carrying a displayable reason rather than queued. The chunk stream's only correlation key is the
    /// conversation, so this is what makes <c>RealtimeChatChunk</c>'s "one in-flight turn per conversation" a
    /// guarantee rather than a comment. The guard wraps the operator-turn persist too, so a refused turn contributes
    /// <b>nothing</b> to the thread. It fails <b>closed</b>: a guard that cannot be evaluated faults the request
    /// rather than degrading into an un-serialized turn.
    /// </para>
    /// <para>
    /// <b>A faulted turn pushes a terminator</b> (gh#1107). The 422 tells the initiating connection; every other
    /// connection is rendering a draft from the chunk stream and would otherwise keep a half-written answer standing
    /// with no error and nothing to retire it. So the <c>!turn.Succeeded</c> branch also pushes
    /// <c>RealtimeChatTurnFaulted</c> — the conversation id (a sufficient key, given the guard above) and a display
    /// reason or none — <b>fail-open</b> like every other push here.
    /// </para>
    /// </remarks>
    /// <param name="id">The conversation to take a turn in.</param>
    /// <param name="request">The operator's message text.</param>
    /// <param name="now">The clock, injected so timestamps and the spend window are testable.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="turnService">Runs the model over the thread and prices the call.</param>
    /// <param name="retrieval">The read-only cross-kind retrieval pipeline for always-on grounding (gh#1065).</param>
    /// <param name="governor">The pure AI-spend gate.</param>
    /// <param name="governorOptions">The daily-budget config (inert when unset).</param>
    /// <param name="ledger">The durable, fail-open AI-usage ledger.</param>
    /// <param name="metrics">The export-only LLM meter.</param>
    /// <param name="notifier">The per-owner realtime notifier.</param>
    /// <param name="turnGuard">Serializes turns per conversation — one in flight at a time (gh#1106).</param>
    /// <param name="loggerFactory">The logger factory (fail-open faults are logged, never silently swallowed).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The persisted turn pair (200), or 400 / 404 / 422 / 429 / 409.</returns>
    internal static async Task<IResult> TurnAsync(
        Guid id,
        ChatTurnRequest? request,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        IChatTurnService turnService,
        IContextRetrievalService retrieval,
        IAiSpendGovernor governor,
        IOptions<GovernorOptions> governorOptions,
        IAiUsageLedger ledger,
        ILlmMetrics metrics,
        IChatRealtimeNotifier notifier,
        IChatTurnGuard turnGuard,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(turnService);
        ArgumentNullException.ThrowIfNull(retrieval);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(governorOptions);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(turnGuard);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Validate the turn text FIRST — a 400 reveals nothing about whether the conversation exists.
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "Message content is required." });
        }
        if (request.Content.Length > ChatMessage.ContentMaxLength)
        {
            return Results.BadRequest(new { error = $"Message content must be at most {ChatMessage.ContentMaxLength} characters." });
        }

        // Hoisted so the guarded body below reads it as non-null: nullable flow analysis does not cross into a local
        // function for a captured variable, and this project is warnings-as-errors.
        string content = request.Content;

        // R-20: a foreign or absent conversation is a 404. The loaded owner is the single authority on whose turn this
        // is — the ledger and the hub push both target it, never request input.
        Conversation? found = await database.Conversations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (found is null)
        {
            return Results.NotFound();
        }

        Conversation conversation = found; // non-null for the guarded body, as above

        ILogger logger = loggerFactory.CreateLogger("MarqSpec.TradingCopilot.Api.Chat.Turn");

        // ONE IN-FLIGHT TURN PER CONVERSATION (gh#1106). Everything from here down runs inside a per-conversation
        // lock the database evaluates — the only thing two separate HTTP requests both observe, which is why a
        // check-then-act here would be no fix at all. Taken NON-BLOCKING: a busy conversation is refused immediately
        // (409 with a reason the operator can read) rather than queued behind a turn that may run for tens of
        // seconds. The operator's turn is persisted INSIDE the callback, so a refused turn contributes nothing.
        //
        // FAIL-CLOSED: a guard that throws propagates. There is deliberately no catch that would fall through to an
        // un-serialized turn — refusing the turn is the safe answer, running a possibly-concurrent one is not.
        return await turnGuard.TryRunExclusiveAsync(database, id, RunTurnAsync, TurnAlreadyInFlight, cancellationToken);

        async Task<IResult> RunTurnAsync()
        {
            // GOVERNOR GATE (gh#448, ADR-0008): cap deployment-wide daily AI spend before the call — inert until a budget
            // is configured. The windowed read crosses R-20 with IgnoreQueryFilters (one shared account funds every user)
            // and is FAIL-OPEN: a spend-read fault leaves the decision null and the turn proceeds un-gated.
            // groundingSuppressed degrades always-on grounding (gh#995) OFF once spend crosses the pre-alert threshold —
            // grounding bills an extra embed + rerank, so it is the first thing to shed as the cap nears, though the chat
            // call itself still runs until the hard cap 429s it below. Fail-open / un-gated leaves it false (grounding on).
            bool groundingSuppressed = false;
            AiSpendBudget? budget = governorOptions.Value.ToBudget();
            if (budget is not null)
            {
                AiSpendDecision? decision = null;
                try
                {
                    decimal spent = await database.AiUsage
                        .IgnoreQueryFilters()
                        .Where(record => record.OccurredAt >= MarketClock.CentralDayStartUtc(now))
                        .Select(record => (decimal?)record.EstimatedCostUsd)
                        .SumAsync(cancellationToken) ?? 0m;
                    decision = governor.Evaluate(budget, spent);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Chat turn could not read AI spend; running this turn un-gated (fail-open).");
                }

                if (decision is { IsBlocked: true })
                {
                    // Nothing persisted, no call made: the operator retries when the daily budget resets.
                    return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status429TooManyRequests);
                }

                groundingSuppressed = decision?.ThresholdReached ?? false;
            }

            // Persist the operator's turn BEFORE the call, so a later LLM fault never loses it (fail-closed keeps it).
            (ChatMessage? userMessage, bool userConflict) = await TryAppendAsync(
                database, conversation, ChatRole.User, content, now, cancellationToken);
            if (userConflict)
            {
                return SequenceConflict();
            }

            // Run the turn over the whole thread in order — the just-persisted user turn is last. A context-window cap is
            // deferred (gh#906); a single operator's thread is bounded in practice.
            List<ChatMessage> history = await database.ChatMessages
                .AsNoTracking()
                .Where(message => message.ConversationId == id)
                .OrderBy(message => message.Sequence)
                .ToListAsync(cancellationToken);

            // Forward each streamed token delta to the owner's connections as it arrives (inc 3b, gh#906) — presentation-
            // only and FAIL-OPEN: a per-chunk push fault is logged and swallowed, so a broken connection never aborts the
            // turn. A genuine caller cancellation still propagates and stops the stream.
            async Task PushDeltaAsync(string delta, CancellationToken chunkToken)
            {
                try
                {
                    await notifier.ChunkAsync(conversation.UserId, new RealtimeChatChunk(id, delta), chunkToken);
                }
                catch (OperationCanceledException) when (chunkToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Chat turn realtime chunk push faulted for owner {Owner}; continuing the turn.", conversation.UserId);
                }
            }

            // ALWAYS-ON CROSS-KIND GROUNDING (gh#1065, of gh#995; R-6, ADR-0027): now the gate has passed and the
            // operator's turn is persisted, retrieve a little context for their message across EVERY retrievable kind and
            // hand it to the model as UNTRUSTED DATA (placed as user-role content, never the system prompt — the service
            // is read-only by construction, and the owner-scoped kinds are R-20-filtered inside it). It is fully
            // FAIL-OPEN: any throw degrades to an un-grounded (history-only) turn, belt-and-suspenders over the pipeline's
            // own degrade-to-empty. It is skipped once spend crossed the pre-alert threshold (grounding bills one embed +
            // one rerank, whatever the number of kinds); no second governor gate runs — the spend it bills is ledgered
            // fail-open and seen by the next turn's floor — and a 429-blocked turn returned above, so it never reaches here.
            IReadOnlyList<RetrievedContextItem> grounding = [];
            if (!groundingSuppressed)
            {
                try
                {
                    grounding = await retrieval.RetrieveAsync(
                        content, GroundingTopK, RetrievalKinds.All, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning(
                        error, "Chat turn grounding faulted for owner {Owner}; running the turn history-only.", conversation.UserId);
                }
            }

            ChatTurnResult turn = await turnService.StreamAsync(history, grounding, PushDeltaAsync, cancellationToken);

            // Meter (export-only, never throws) and ledger (durable, FAIL-OPEN) EVERY model call the turn made — a
            // tool-using turn makes several (gh#925) — success OR failure, so the governor floor sees each billed call.
            // The owner is the conversation's (R-20); the clock is the turn's now. A ledger fault on one call is logged and
            // the rest still record (fail-open); the turn stands regardless.
            foreach (AiCallCost cost in turn.Costs)
            {
                metrics.RecordLlmCall(cost);
                try
                {
                    await ledger.RecordAsync(
                        new AiUsageEntry(conversation.UserId, cost, Activity.Current?.TraceId.ToString(), now),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Chat turn AI-usage ledger write faulted for owner {Owner}; the turn stands.", conversation.UserId);
                }
            }

            // Fail-closed: a refused / truncated / faulted turn is a 422 with the reason. The operator's turn stays saved
            // and the cost recorded, but no assistant turn is invented.
            if (!turn.Succeeded)
            {
                // TERMINATE THE DRAFT EVERYWHERE (gh#1107). A faulted turn can have streamed a whole round before it
                // failed — only round 1 streams, so "partial text then silence" is its ordinary shape — and the 422
                // below reaches only the connection that sent it. Every other screen is rendering that partial answer
                // and, without this, would keep it standing with no error and nothing that would ever retire it. The
                // conversation id is a sufficient key because at most one turn is in flight on it (the guard above),
                // which is why no turn id rides this wire. FAIL-OPEN like every other push here: presentation-only,
                // and the 422 the caller gets is unchanged whatever the hub does (ADR-0021).
                try
                {
                    await notifier.TurnFaultedAsync(
                        conversation.UserId,
                        new RealtimeChatTurnFaulted(id, Normalize(turn.Message)),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Chat turn faulted-terminator push faulted for owner {Owner}; the 422 stands.", conversation.UserId);
                }

                return Results.UnprocessableEntity(new { error = turn.Message });
            }

            (ChatMessage? assistantMessage, bool assistantConflict) = await TryAppendAsync(
                database, conversation, ChatRole.Assistant, turn.Message, now, cancellationToken);
            if (assistantConflict)
            {
                // Streamed, then no terminator — the same shape gh#1107 closes above, but not reachable by a second
                // TURN any more (the guard serializes those). What is left is `POST /messages` taking the sequence
                // mid-turn, which is rare enough that it is left to the client's idle backstop rather than given a
                // signal of its own; it is untestable at the unit tier, since the in-memory provider enforces no
                // unique index, and this project does not ship production behaviour no test drives.
                return SequenceConflict();
            }

            // Presentation-only push to the owner's OTHER connections, AFTER the write commits; its failure never fails
            // the turn (the REST response below already carries the answer to the initiating caller).
            try
            {
                await notifier.MessageAppendedAsync(
                    conversation.UserId,
                    new RealtimeChatMessage(
                        id, assistantMessage!.Id, assistantMessage.Sequence, assistantMessage.Role,
                        assistantMessage.Content, assistantMessage.CreatedAt),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Chat turn realtime push faulted for owner {Owner}; the turn stands.", conversation.UserId);
            }

            return Results.Ok(new ChatTurnResponse(
                ChatMessageResponse.From(userMessage!), ChatMessageResponse.From(assistantMessage!)));
        }
    }

    /// <summary>
    /// Appends one message to a conversation with the shared write guards (gh#900 review, reused by gh#906): the owner
    /// is the loaded <paramref name="conversation"/>'s (never request input), the <c>Sequence</c> is allocated
    /// <c>max+1</c>, and the unique <c>(ConversationId, Sequence)</c> index is the backstop against a concurrent race.
    /// </summary>
    /// <returns>The appended message, or <c>(null, true)</c> when the sequence race lost — the caller returns a 409.</returns>
    private static async Task<(ChatMessage? Message, bool Conflict)> TryAppendAsync(
        TradingCopilotDbContext database,
        Conversation conversation,
        ChatRole role,
        string content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The max+1 read is not itself race-free — two concurrent appends can read the same max — so the unique index
        // is what actually keeps the thread integral; a loser is caught below and surfaced as a 409.
        int highest = await database.ChatMessages
            .Where(message => message.ConversationId == conversation.Id)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        ChatMessage message = new()
        {
            Id = Guid.NewGuid(),
            UserId = conversation.UserId, // the CONVERSATION's owner, read under R-20 — never request input
            ConversationId = conversation.Id,
            Sequence = highest + 1,
            Role = role,
            Content = content,
            CreatedAt = now,
        };
        database.ChatMessages.Add(message);
        conversation.UpdatedAt = now; // bump so the conversation-list read surfaces the freshest thread first

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return (message, false);
        }
        catch (DbUpdateException)
        {
            // The unique (ConversationId, Sequence) index rejected a concurrent append that took this position. Every
            // other constraint (the FK to the loaded conversation, the Role / Sequence CHECKs) is pre-satisfied, so a
            // violation here is the sequence race. Detach the failed entity so the context stays usable; QA proves the
            // collision against real Postgres.
            database.Entry(message).State = EntityState.Detached;
            return (null, true);
        }
    }

    // The shared 409 for a lost sequence race — rare in a single-operator deployment.
    private static IResult SequenceConflict() => Results.Conflict(new
    {
        error = "A concurrent message took that position in the conversation; retry.",
    });

    // The 409 for a second concurrent turn on one conversation (gh#1106). 409 rather than 422 deliberately: this
    // endpoint already spends 422 on "the turn ran and could not produce an answer", and a client that could not
    // tell the two apart would show the wrong affordance for each — a retry hint where an apology belongs, or the
    // reverse. 409 is what this endpoint already means by "a concurrent request took this; retry", which is exactly
    // this case. The reason is written to be displayed as-is.
    private static IResult TurnAlreadyInFlight() => Results.Conflict(new
    {
        error = "A turn is already in flight on this conversation; wait for it to finish, then retry.",
    });

    // A blank title is no title: trim, and collapse empty/whitespace to null so "" and "   " are not stored as a title.
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
