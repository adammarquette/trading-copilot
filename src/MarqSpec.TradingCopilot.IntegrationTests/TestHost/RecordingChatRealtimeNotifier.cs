using MarqSpec.TradingCopilot.Api.Realtime;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// What the chat realtime seam was asked to push, per owner (gh#930). Written by
/// <see cref="RecordingChatRealtimeNotifier"/>, read by a suite.
/// </summary>
public sealed class ChatRealtimeRecorder
{
    private readonly Lock _gate = new();
    private readonly List<(Guid OwnerId, RealtimeChatMessage Message)> _messages = [];
    private readonly List<(Guid OwnerId, RealtimeChatChunk Chunk)> _chunks = [];
    private readonly List<(Guid OwnerId, RealtimeChatTurnFaulted Faulted)> _faults = [];

    /// <summary>Every committed message push, with the owner it was routed to.</summary>
    public IReadOnlyList<(Guid OwnerId, RealtimeChatMessage Message)> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>Every streamed token-delta push, with the owner it was routed to.</summary>
    public IReadOnlyList<(Guid OwnerId, RealtimeChatChunk Chunk)> Chunks
    {
        get
        {
            lock (_gate)
            {
                return [.. _chunks];
            }
        }
    }

    /// <summary>Every faulted-turn terminator push, with the owner it was routed to (gh#1107).</summary>
    public IReadOnlyList<(Guid OwnerId, RealtimeChatTurnFaulted Faulted)> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _faults];
            }
        }
    }

    /// <summary>Clears every recording — call at the start of each test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _messages.Clear();
            _chunks.Clear();
            _faults.Clear();
        }
    }

    internal void RecordMessage(Guid ownerId, RealtimeChatMessage message)
    {
        lock (_gate)
        {
            _messages.Add((ownerId, message));
        }
    }

    internal void RecordChunk(Guid ownerId, RealtimeChatChunk chunk)
    {
        lock (_gate)
        {
            _chunks.Add((ownerId, chunk));
        }
    }

    internal void RecordTurnFaulted(Guid ownerId, RealtimeChatTurnFaulted faulted)
    {
        lock (_gate)
        {
            _faults.Add((ownerId, faulted));
        }
    }
}

/// <summary>
/// A <b>pass-through recorder</b> around the production <see cref="IChatRealtimeNotifier"/> (gh#930) — deliberately a
/// decorator, not a stub.
/// </summary>
/// <remarks>
/// The realtime seam is in-process (SignalR), not an outbound third party, so this tier does not get to replace it:
/// substituting it would delete the production notifier and its per-owner <c>Clients.User</c> routing from the
/// composition under test, and "the reply is pushed per-owner" would then be a claim about the double. Wrapping keeps
/// the shipped notifier and the real hub on the path — every call still goes through them, and a broken hub
/// composition still fails — while making the owner each push was routed to observable to a suite.
/// </remarks>
public sealed class RecordingChatRealtimeNotifier : IChatRealtimeNotifier
{
    private readonly IChatRealtimeNotifier _inner;
    private readonly ChatRealtimeRecorder _recorder;

    /// <summary>Wraps the production notifier, recording into <paramref name="recorder"/>.</summary>
    /// <param name="inner">The production notifier — still called, never bypassed.</param>
    /// <param name="recorder">Where the per-owner pushes are recorded.</param>
    public RecordingChatRealtimeNotifier(IChatRealtimeNotifier inner, ChatRealtimeRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);
        _inner = inner;
        _recorder = recorder;
    }

    /// <inheritdoc />
    public Task MessageAppendedAsync(Guid ownerId, RealtimeChatMessage message, CancellationToken cancellationToken)
    {
        _recorder.RecordMessage(ownerId, message);
        return _inner.MessageAppendedAsync(ownerId, message, cancellationToken);
    }

    /// <inheritdoc />
    public Task ChunkAsync(Guid ownerId, RealtimeChatChunk chunk, CancellationToken cancellationToken)
    {
        _recorder.RecordChunk(ownerId, chunk);
        return _inner.ChunkAsync(ownerId, chunk, cancellationToken);
    }

    /// <inheritdoc />
    public Task TurnFaultedAsync(Guid ownerId, RealtimeChatTurnFaulted faulted, CancellationToken cancellationToken)
    {
        _recorder.RecordTurnFaulted(ownerId, faulted);
        return _inner.TurnFaultedAsync(ownerId, faulted, cancellationToken);
    }
}
