namespace MarqSpec.TradingCopilot.Api.Chat;

/// <summary>
/// Which conversation the request is taking a turn in (gh#1135 of gh#1059, R-7) — the one piece of turn identity a
/// chat <b>write</b> tool needs and cannot otherwise reach, since <c>IChatTool.ExecuteAsync</c> receives only the
/// model's own JSON.
/// </summary>
/// <remarks>
/// <para>
/// It exists so an <c>edit_rulebook</c> write carries its <b>provenance</b>: a chat-authored trigger stamps
/// <c>TriggerRecord.SourceConversationId</c> (gh#471), which is what makes "why does this rule exist?" answerable
/// without a database walk — and what distinguishes a model-authored rule from one the operator wrote over the API.
/// Passing it through <c>IChatTool</c>'s signature would change every read tool for one write tool's benefit; a
/// request-scoped ambient value does not.
/// </para>
/// <para>
/// <b>Required, never optional.</b> A tool takes this as a hard constructor dependency and <b>fails closed</b> when
/// <see cref="ConversationId"/> is still <see langword="null"/> — a chat tool only ever runs inside a turn, so a null
/// here means the endpoint did not enter the scope, i.e. the wiring is broken. An optional dependency defaulting to
/// "no provenance" would instead write an <i>unattributed</i> rule and look like it worked.
/// </para>
/// <para>
/// It carries the conversation and nothing else. It is deliberately <b>not</b> a general per-turn bag: a seam that
/// accumulates state is how a tool eventually reaches something it should not, and the pinned constructor allow-list
/// in <c>ChatToolBoundaryTests</c> can only vouch for what this type can hand out.
/// </para>
/// </remarks>
public interface IChatTurnScope
{
    /// <summary>The conversation this turn belongs to, or <see langword="null"/> outside a chat turn.</summary>
    Guid? ConversationId { get; }

    /// <summary>Marks the request as taking a turn in <paramref name="conversationId"/>. Called once, by the turn endpoint.</summary>
    /// <param name="conversationId">The conversation the turn runs in.</param>
    void EnterConversation(Guid conversationId);
}

/// <summary>The request-scoped <see cref="IChatTurnScope"/>. Registered <b>scoped</b>: its whole value is per-request.</summary>
internal sealed class ChatTurnScope : IChatTurnScope
{
    /// <inheritdoc />
    public Guid? ConversationId { get; private set; }

    /// <inheritdoc />
    public void EnterConversation(Guid conversationId) => ConversationId = conversationId;
}
