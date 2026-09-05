using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// A newly appended chat message, pushed to the owning operator over the realtime hub (gh#906, R-6). A chat message
/// is <b>operator-owned</b> (R-20), so it is routed with <c>Clients.User</c> — never
/// broadcast. <b>Presentation-only</b> (ADR-0021): the message already committed to the database and is also returned
/// on the REST turn response; this pushes the same assistant turn to the operator's <i>other</i> connections (the
/// multi-screen workspace, ADR-0006), so a turn taken on one surface appears on all. The wire is a signal to
/// reconcile, never the source of truth (the REST read model is).
/// </summary>
/// <param name="ConversationId">The conversation the message belongs to.</param>
/// <param name="MessageId">The message's id.</param>
/// <param name="Sequence">Its 1-based position in the thread.</param>
/// <param name="Role">Who authored it (the co-pilot turn is <see cref="ChatRole.Assistant"/>).</param>
/// <param name="Content">The message text — untrusted display data, never re-injected as instruction.</param>
/// <param name="At">When it was created.</param>
public sealed record RealtimeChatMessage(
    Guid ConversationId,
    Guid MessageId,
    int Sequence,
    ChatRole Role,
    string Content,
    DateTimeOffset At)
{
    /// <summary>The client method the hub invokes to deliver an appended chat message.</summary>
    public const string ClientMethod = "realtimeChatMessage";
}

/// <summary>
/// One streamed token delta of an in-flight assistant turn (gh#906 inc 3b), pushed to the owning operator as the
/// model generates. <b>Presentation-only</b> and best-effort: a client renders a live draft from the deltas and
/// then swaps it for the canonical <see cref="RealtimeChatMessage"/> when the turn commits. A dropped chunk is
/// never fatal — the REST turn response and the final message are the source of truth, not the delta stream.
/// </summary>
/// <param name="ConversationId">
/// The conversation whose turn is streaming — the correlation key, and a <b>sufficient</b> one: the server refuses a
/// second concurrent turn on a conversation (<see cref="MarqSpec.TradingCopilot.Api.Chat.IChatTurnGuard"/>, gh#1106),
/// so one in-flight turn per conversation is a guarantee it keeps, not an assumption a client has to hope for.
/// </param>
/// <param name="Delta">The incremental text — untrusted display data, never re-injected as instruction.</param>
public sealed record RealtimeChatChunk(Guid ConversationId, string Delta)
{
    /// <summary>The client method the hub invokes to deliver a streamed token delta.</summary>
    public const string ClientMethod = "realtimeChatChunk";
}

/// <summary>
/// The terminator of a chat turn that <b>faulted</b> (gh#1107): a refused / truncated / provider-faulted turn, which
/// streams its first round and then produces no assistant message at all. The initiating connection learns of it from
/// the endpoint's 422; without this every OTHER connection would keep the half-written draft standing forever, with
/// no error and nothing that would ever retire it. <b>Presentation-only</b> and <b>fail-open</b> exactly like the
/// chunk and message pushes (ADR-0021): a hub fault never changes the turn's outcome or the HTTP response.
/// </summary>
/// <param name="ConversationId">
/// The conversation whose turn faulted — and a sufficient correlation key on its own, because at most one turn is in
/// flight on a conversation (gh#1106). There is deliberately <b>no turn id</b> on this wire for that reason.
/// </param>
/// <param name="Reason">
/// A short display reason — <b>the same text the initiator sees in the 422</b>, so two screens of one desk never
/// disagree about why an answer stopped. <c>ChatEndpoints</c> always states one (falling back to
/// <c>ChatEndpoints.FaultedTurnFallbackReason</c> when the turn itself carried nothing displayable), because a
/// terminator that retires a half-written answer and explains nothing is the honest-states gap R-19 / ADR-0013
/// rule out. The type stays nullable as wire tolerance, not as a state this producer emits — a consumer should
/// still retire the draft if it ever arrives without one. Untrusted display data like every other content field:
/// render it, never re-issue it as instruction.
/// </param>
public sealed record RealtimeChatTurnFaulted(Guid ConversationId, string? Reason)
{
    /// <summary>The client method the hub invokes to deliver a faulted turn's terminator.</summary>
    public const string ClientMethod = "realtimeChatTurnFaulted";
}

/// <summary>
/// Pushes an operator's own newly appended chat messages to their realtime connections (gh#906). <b>Read-side
/// only</b>: it is called AFTER the message write commits, and its failure never affects that write. Routing is
/// per-owner via <c>Clients.User(ownerId)</c> — chat is operator-owned (R-20), never broadcast — which resolves
/// through <see cref="RealtimeUserIdProvider"/> (the connection's <c>sub</c> claim). The sibling of
/// <see cref="ISuggestionRealtimeNotifier"/> for the chat turn.
/// </summary>
public interface IChatRealtimeNotifier
{
    /// <summary>Pushes a newly appended message to the owning operator's connections.</summary>
    /// <param name="ownerId">The conversation's owner (R-20) — the only recipient.</param>
    /// <param name="message">The appended message.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task MessageAppendedAsync(Guid ownerId, RealtimeChatMessage message, CancellationToken cancellationToken);

    /// <summary>Pushes one streamed token delta of an in-flight turn to the owning operator's connections (inc 3b).</summary>
    /// <param name="ownerId">The conversation's owner (R-20) — the only recipient.</param>
    /// <param name="chunk">The streamed delta.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task ChunkAsync(Guid ownerId, RealtimeChatChunk chunk, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes the terminator of a <b>faulted</b> turn to the owning operator's connections (gh#1107), so every screen
    /// retires its draft at the moment the sender does rather than keeping a half-written answer standing.
    /// </summary>
    /// <param name="ownerId">The conversation's owner (R-20) — the only recipient.</param>
    /// <param name="faulted">The faulted turn's conversation, and a display reason or none.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task TurnFaultedAsync(Guid ownerId, RealtimeChatTurnFaulted faulted, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class ChatRealtimeNotifier : IChatRealtimeNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;

    /// <summary>Creates the notifier over the hub context it pushes through.</summary>
    public ChatRealtimeNotifier(IHubContext<RealtimeHub> hub) => _hub = hub;

    /// <inheritdoc />
    public Task MessageAppendedAsync(Guid ownerId, RealtimeChatMessage message, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeChatMessage.ClientMethod, message, cancellationToken);

    /// <inheritdoc />
    public Task ChunkAsync(Guid ownerId, RealtimeChatChunk chunk, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeChatChunk.ClientMethod, chunk, cancellationToken);

    /// <inheritdoc />
    public Task TurnFaultedAsync(Guid ownerId, RealtimeChatTurnFaulted faulted, CancellationToken cancellationToken) =>
        _hub.Clients.User(ownerId.ToString()).SendAsync(RealtimeChatTurnFaulted.ClientMethod, faulted, cancellationToken);
}
