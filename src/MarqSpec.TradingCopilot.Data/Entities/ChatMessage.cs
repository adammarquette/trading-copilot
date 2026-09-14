using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Chat;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One message in a <see cref="Conversation"/> (gh#18, R-6) — its author, its text, and its position in the
/// thread. Operator-owned (R-20): it carries its own <see cref="UserId"/>, so a direct query is scoped too, not
/// only reads that join through the conversation — a message can never leak even if a query forgets the join.
/// </summary>
public class ChatMessage : IUserOwned
{
    /// <summary>How long a single message may be (characters) — a generous cap for a long model reply, still bounded.</summary>
    public const int ContentMaxLength = 32_768;

    /// <summary>The message's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — the same owner as the conversation; scopes a direct message query.</summary>
    public Guid UserId { get; set; }

    /// <summary>The conversation this message belongs to.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The message's 1-based position in the thread — the total order the conversation reads by. A DB check refuses
    /// a non-positive value, and a unique <c>(ConversationId, Sequence)</c> index refuses two messages at one position.
    /// </summary>
    public required int Sequence { get; set; }

    /// <summary>Who authored it. <see cref="ChatRole.Unknown"/> is refused by a DB check.</summary>
    public required ChatRole Role { get; set; }

    /// <summary>The message text, length-capped (<see cref="ContentMaxLength"/>).</summary>
    /// <remarks>
    /// <b>Untrusted display data.</b> A user turn is operator input and an assistant turn is model-authored prose;
    /// neither is re-injected into a later prompt as instruction beyond the deliberate conversation history, and
    /// neither is rendered as markup.
    /// </remarks>
    public required string Content { get; set; }

    /// <summary>When the message was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
