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
}
