using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A co-pilot chat conversation (gh#18, R-6) — the thread a session's <see cref="ChatMessage"/>s belong to, and
/// the origin a trigger can reference so a rule's provenance stays auditable (gh#471). Operator-owned (R-20).
/// </summary>
public class Conversation : IUserOwned
{
    /// <summary>The conversation's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// A short human title, or <see langword="null"/> until the conversation is named (the model or the operator
    /// may set one). Display data — never re-injected into a prompt as instruction.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>When the conversation was started.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the conversation last changed — bumped as messages are appended, so the conversation list can read
    /// most-recent-first. Stamped by the writer atomically with the append.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
