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
            IChatTurnService turnService, IAiSpendGovernor governor, IOptions<GovernorOptions> governorOptions,
            IAiUsageLedger ledger, ILlmMetrics metrics, IChatRealtimeNotifier notifier, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            TurnAsync(id, request, DateTimeOffset.UtcNow, database, turnService, governor, governorOptions, ledger,
                metrics, notifier, loggerFactory, cancellationToken))
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
    /// <b>Presentation-only push.</b> On success the reply is pushed per-owner over the hub (ADR-0021) <b>after</b> the
    /// write commits, and that push's failure never fails the turn — the REST response already carries the answer to
    /// the initiating caller. Token streaming (3b) extends the provider seam and is deferred (gh#906).
    /// </para>
    /// </remarks>
    /// <param name="id">The conversation to take a turn in.</param>
    /// <param name="request">The operator's message text.</param>
    /// <param name="now">The clock, injected so timestamps and the spend window are testable.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="turnService">Runs the model over the thread and prices the call.</param>
    /// <param name="governor">The pure AI-spend gate.</param>
    /// <param name="governorOptions">The daily-budget config (inert when unset).</param>
    /// <param name="ledger">The durable, fail-open AI-usage ledger.</param>
    /// <param name="metrics">The export-only LLM meter.</param>
    /// <param name="notifier">The per-owner realtime notifier.</param>
    /// <param name="loggerFactory">The logger factory (fail-open faults are logged, never silently swallowed).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The persisted turn pair (200), or 400 / 404 / 422 / 429 / 409.</returns>
    internal static async Task<IResult> TurnAsync(
        Guid id,
        ChatTurnRequest? request,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        IChatTurnService turnService,
        IAiSpendGovernor governor,
        IOptions<GovernorOptions> governorOptions,
        IAiUsageLedger ledger,
        ILlmMetrics metrics,
        IChatRealtimeNotifier notifier,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(turnService);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(governorOptions);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(notifier);
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

        // R-20: a foreign or absent conversation is a 404. The loaded owner is the single authority on whose turn this
        // is — the ledger and the hub push both target it, never request input.
        Conversation? conversation = await database.Conversations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        ILogger logger = loggerFactory.CreateLogger("MarqSpec.TradingCopilot.Api.Chat.Turn");

        // GOVERNOR GATE (gh#448, ADR-0008): cap deployment-wide daily AI spend before the call — inert until a budget
        // is configured. The windowed read crosses R-20 with IgnoreQueryFilters (one shared account funds every user)
        // and is FAIL-OPEN: a spend-read fault leaves the decision null and the turn proceeds un-gated.
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
        }

        // Persist the operator's turn BEFORE the call, so a later LLM fault never loses it (fail-closed keeps it).
        (ChatMessage? userMessage, bool userConflict) = await TryAppendAsync(
            database, conversation, ChatRole.User, request.Content, now, cancellationToken);
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

        ChatTurnResult turn = await turnService.StreamAsync(history, PushDeltaAsync, cancellationToken);

        // Meter (export-only, never throws) and ledger (durable, FAIL-OPEN) the call — success OR failure, so the
        // governor floor sees every billed turn. The owner is the conversation's (R-20); the clock is the turn's now.
        metrics.RecordLlmCall(turn.Cost);
        try
        {
            await ledger.RecordAsync(
                new AiUsageEntry(conversation.UserId, turn.Cost, Activity.Current?.TraceId.ToString(), now),
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

        // Fail-closed: a refused / truncated / faulted turn is a 422 with the reason. The operator's turn stays saved
        // and the cost recorded, but no assistant turn is invented.
        if (!turn.Succeeded)
        {
            return Results.UnprocessableEntity(new { error = turn.Message });
        }

        (ChatMessage? assistantMessage, bool assistantConflict) = await TryAppendAsync(
            database, conversation, ChatRole.Assistant, turn.Message, now, cancellationToken);
        if (assistantConflict)
        {
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

    // A blank title is no title: trim, and collapse empty/whitespace to null so "" and "   " are not stored as a title.
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
