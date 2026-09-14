using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Chat;

namespace MarqSpec.TradingCopilot.Api.Chat;

/// <summary>Start a conversation. An optional human title; the thread can be named later.</summary>
/// <param name="Title">A short title, or null for an untitled conversation.</param>
public sealed record CreateConversationRequest(string? Title);

/// <summary>A conversation as returned to the operator (no messages — see <see cref="ConversationDetailResponse"/>).</summary>
/// <param name="Id">The conversation's id.</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="UpdatedAt">When it last changed (bumped on each appended message).</param>
public sealed record ConversationResponse(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a persisted conversation to the response shape.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The response.</returns>
    public static ConversationResponse From(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new ConversationResponse(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt);
    }
}

/// <summary>A page of the operator's conversations.</summary>
/// <param name="Conversations">The conversations, most-recent-first.</param>
public sealed record ConversationListResponse(IReadOnlyList<ConversationResponse> Conversations);

/// <summary>A conversation with its messages, in <c>Sequence</c> order.</summary>
/// <param name="Id">The conversation's id.</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Messages">Its messages, ordered by sequence.</param>
public sealed record ConversationDetailResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessageResponse> Messages)
{
    /// <summary>Projects a conversation and its ordered messages to the detail shape.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="messages">Its messages, already ordered by sequence.</param>
    /// <returns>The response.</returns>
    public static ConversationDetailResponse From(Conversation conversation, IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);
        return new ConversationDetailResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            [.. messages.Select(ChatMessageResponse.From)]);
    }
}

/// <summary>
/// Append a message to a conversation. It carries <b>no owner</b> — the server derives the message's owner from the
/// conversation (R-20), so a client can never write a message scoped to someone else.
/// </summary>
/// <param name="Role">Who authored the message (User / Assistant / System; never Unknown).</param>
/// <param name="Content">The message text.</param>
public sealed record AppendMessageRequest(ChatRole Role, string Content);

/// <summary>A chat message as returned to the operator.</summary>
/// <param name="Id">The message's id.</param>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="Sequence">Its 1-based position in the thread.</param>
/// <param name="Role">Who authored it.</param>
/// <param name="Content">The message text.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record ChatMessageResponse(
    Guid Id,
    Guid ConversationId,
    int Sequence,
    ChatRole Role,
    string Content,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a persisted message to the response shape.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The response.</returns>
    public static ChatMessageResponse From(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessageResponse(
            message.Id, message.ConversationId, message.Sequence, message.Role, message.Content, message.CreatedAt);
    }
}

/// <summary>
/// Take a grounded co-pilot chat turn (gh#906): the operator's new message. The server appends it as the
/// <see cref="ChatRole.User"/> turn, runs the model over the thread, and appends the reply — so the request carries
/// only the text, never a role or owner.
/// </summary>
/// <param name="Content">The operator's message text.</param>
public sealed record ChatTurnRequest(string Content);

/// <summary>
/// The result of a successful chat turn: the operator's persisted turn and the co-pilot's reply, both with their
/// allocated sequence. A refused / faulted turn returns an error status instead (the user turn is still saved).
/// </summary>
/// <param name="UserMessage">The operator's message, as persisted.</param>
/// <param name="AssistantMessage">The co-pilot's reply, as persisted.</param>
public sealed record ChatTurnResponse(ChatMessageResponse UserMessage, ChatMessageResponse AssistantMessage);
