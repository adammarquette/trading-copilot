using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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

        // GUARD 2 (gh#900 review): allocate the next sequence; the unique (ConversationId, Sequence) index is the
        // backstop. This max+1 read is not itself race-free — two concurrent appends can read the same max — so the
        // index is what actually keeps the thread integral; a loser is caught below and returned as a 409.
        int highest = await database.ChatMessages
            .Where(message => message.ConversationId == id)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        ChatMessage appended = new()
        {
            Id = Guid.NewGuid(),
            // GUARD 1 (gh#900 review): the owner is the CONVERSATION's, read under R-20 above — never request input
            // (AppendMessageRequest has no UserId). Mirrors SuggestionEndpoints.PassAsync (UserId = suggestion.UserId).
            UserId = conversation.UserId,
            ConversationId = id,
            Sequence = highest + 1,
            Role = request.Role,
            Content = request.Content,
            CreatedAt = now,
        };
        database.ChatMessages.Add(appended);
        conversation.UpdatedAt = now; // bump so the conversation-list read surfaces the freshest thread first

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique (ConversationId, Sequence) index rejected a concurrent append that took this position. The FK
            // to the just-loaded conversation and the Role/Sequence CHECKs are all pre-satisfied, so a violation here
            // is the sequence race. Rare in a single-operator deployment; QA proves the collision against real Postgres.
            return Results.Conflict(new
            {
                error = "A concurrent message took that position in the conversation; retry.",
            });
        }

        return Results.Ok(ChatMessageResponse.From(appended));
    }

    // A blank title is no title: trim, and collapse empty/whitespace to null so "" and "   " are not stored as a title.
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
