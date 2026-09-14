using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>How the system reached the model — the two <see cref="ILlmProvider"/> entry points.</summary>
public enum LlmCallKind
{
    /// <summary>Via <see cref="ILlmProvider.StreamAsync"/> — the turn's round-1 call.</summary>
    Stream = 1,

    /// <summary>Via <see cref="ILlmProvider.CompleteAsync"/> — a tool-loop round.</summary>
    Complete = 2,
}

/// <summary>One model call the system made, exactly as it was issued.</summary>
/// <param name="Kind">Which entry point was used.</param>
/// <param name="Request">The request the system built — the offered tools and the fed-back conversation.</param>
public sealed record RecordedLlmCall(LlmCallKind Kind, LlmRequest Request);

/// <summary>
/// A <b>scripted, adversarial</b> stand-in for the model on the chat-turn path (gh#930, for gh#925). Like
/// <see cref="AdversarialLlmProvider"/> beside it, it doubles the one outbound third-party seam this tier may double —
/// but it is built for the <b>tool loop</b>: a step is a function of the request, so a scripted step can ask for a
/// tool, read what the loop fed back, and answer <i>from that</i>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It feeds inputs, never the production-computed answer.</b> A step that wants to answer with journal data has to
/// build it out of the <see cref="LlmToolResult"/>s the loop handed back (<see cref="ToolResultsIn"/>). So a suite's
/// "the turn is grounded" assertion cannot pass unless the tool actually ran, against the real database, and its
/// result actually re-entered the conversation — the provider has no other source for that text.
/// </para>
/// <para>
/// <b>It is adversarial where it matters.</b> Nothing here consults <see cref="LlmRequest.Tools"/> before naming a
/// tool: a step may demand a tool that was never offered — including an order-shaped one — exactly as a real model
/// could hallucinate one. The system's refusal to dispatch it is then a property of the system, not of the double.
/// </para>
/// <para>
/// <b>A runaway loop trips rather than hangs.</b> Past <see cref="RunawayThreshold"/> calls the provider throws and
/// latches <see cref="RunawayTripped"/>, so a broken round cap surfaces as a <i>red assertion</i> instead of a suite
/// that never returns (a hanging test proves nothing and blocks CI).
/// </para>
/// </remarks>
public sealed class ScriptedChatLlmProvider : ILlmProvider
{
    /// <summary>
    /// The call count past which the provider refuses to answer. Comfortably above any legitimate bounded turn
    /// (one stream + the service's round cap), so it can only be reached by a loop that is genuinely unbounded.
    /// </summary>
    public const int RunawayThreshold = 24;

    private readonly Lock _gate = new();
    private readonly Queue<Func<LlmRequest, LlmCompletion>> _script = new();
    private readonly List<RecordedLlmCall> _calls = [];
    private Func<LlmRequest, LlmCompletion>? _always;
    private bool _runawayTripped;
    private int _unscriptedCalls;
    private Func<Task>? _onCall;

    /// <summary>Every model call the system made, in order — the offered tools and fed-back results are on each request.</summary>
    public IReadOnlyList<RecordedLlmCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many model calls the turn made — the per-call ledger and the round cap are both counted here.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>
    /// <see langword="true"/> once the system pushed past <see cref="RunawayThreshold"/> calls — i.e. the tool loop is
    /// <b>not</b> bounded. A suite asserts this is false; it is the bounded-loop guard's red.
    /// </summary>
    public bool RunawayTripped
    {
        get
        {
            lock (_gate)
            {
                return _runawayTripped;
            }
        }
    }

    /// <summary>
    /// How many calls arrived with the script exhausted. Non-zero means the turn made a call the test did not expect,
    /// which the service's fail-closed catch would otherwise hide behind a generic refusal.
    /// </summary>
    public int UnscriptedCalls
    {
        get
        {
            lock (_gate)
            {
                return _unscriptedCalls;
            }
        }
    }

    /// <summary>Clears the script and every recording — call at the start of each test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _script.Clear();
            _calls.Clear();
            _always = null;
            _runawayTripped = false;
            _unscriptedCalls = 0;
            _onCall = null;
        }
    }

    /// <summary>
    /// Runs <paramref name="effect"/> inside every model call (<see cref="StreamAsync"/> and
    /// <see cref="CompleteAsync"/>), after the scripted completion is computed but before it is returned — the seam
    /// gh#1118's chat-turn-guard concurrency suite uses to launch a peer request while <c>ChatTurnGuard</c>'s
    /// per-conversation advisory lock is still held (the SQL unlock runs only once the whole turn this model call is
    /// part of has returned). Mirrors <see cref="AdversarialTestProjectXVenueFactory.OnPlaceOrder"/>. Unlike that
    /// blocking-lock seam, a peer launched here may be safely <b>awaited inline</b>: the chat-turn guard's own check
    /// is non-blocking (<c>pg_try_advisory_lock</c> fails fast rather than parking), so a peer racing the SAME
    /// conversation can never deadlock on the lock under test. Passing <see langword="null"/> clears it.
    /// </summary>
    /// <param name="effect">The interleave to run, or <see langword="null"/> to clear it.</param>
    public void OnCall(Func<Task>? effect) => _onCall = effect;

    /// <summary>Appends one scripted step, consumed in call order.</summary>
    /// <param name="step">Builds the completion from the request the system issued.</param>
    public void Script(Func<LlmRequest, LlmCompletion> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            _script.Enqueue(step);
        }
    }

    /// <summary>Appends one scripted step that ignores the request.</summary>
    /// <param name="completion">The completion to return.</param>
    public void Script(LlmCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        Script(_ => completion);
    }

    /// <summary>
    /// Answers <b>every</b> call with <paramref name="step"/> once the queued script is exhausted — the "the model never
    /// stops asking for tools" case the round cap exists to stop.
    /// </summary>
    /// <param name="step">Builds the completion from the request.</param>
    public void ScriptAlways(Func<LlmRequest, LlmCompletion> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            _always = step;
        }
    }

    /// <summary>A clean final answer.</summary>
    /// <param name="text">The answer text.</param>
    /// <param name="usage">The tokens this call reports; defaults to a non-zero count so a ledgered cost is never a vacuous $0.00.</param>
    /// <returns>A <see cref="LlmStopReason.Completed"/> completion.</returns>
    public static LlmCompletion Answer(string text, LlmUsage? usage = null) =>
        new(text, LlmStopReason.Completed, usage ?? new LlmUsage(1_000, 200));

    /// <summary>
    /// A <c>tool_use</c> stop naming <paramref name="toolName"/>. <b>Nothing checks whether that tool was offered</b> —
    /// that is the point: a suite hands this an order-shaped name and the system must refuse to dispatch it.
    /// </summary>
    /// <param name="toolName">The tool the model demands — offered or not.</param>
    /// <param name="inputJson">The tool input as the model would send it.</param>
    /// <param name="callId">The provider-side id echoed back on the result.</param>
    /// <param name="usage">The tokens this call reports.</param>
    /// <returns>A <see cref="LlmStopReason.ToolUse"/> completion carrying the one call.</returns>
    public static LlmCompletion WantsTool(
        string toolName, string inputJson = "{}", string callId = "call-1", LlmUsage? usage = null) =>
        new(
            string.Empty,
            LlmStopReason.ToolUse,
            usage ?? new LlmUsage(1_200, 60),
            [new LlmToolCall(callId, toolName, inputJson)]);

    /// <summary>
    /// A bare <c>tool_use</c> stop with no parsed calls — what the streaming round-1 call reports before the loop
    /// re-issues to recover the tool blocks.
    /// </summary>
    /// <param name="usage">The tokens this call reports.</param>
    /// <returns>A <see cref="LlmStopReason.ToolUse"/> completion with no tool calls.</returns>
    public static LlmCompletion SignalsToolUse(LlmUsage? usage = null) =>
        new(string.Empty, LlmStopReason.ToolUse, usage ?? new LlmUsage(900, 40), []);

    /// <summary>
    /// The tool results the loop fed back on this request — the <b>only</b> channel through which real data reaches a
    /// scripted answer, and the channel a suite reads to see what the loop did with a tool call.
    /// </summary>
    /// <param name="request">The request the system issued.</param>
    /// <returns>Every tool result carried by the conversation, oldest first.</returns>
    public static IReadOnlyList<LlmToolResult> ToolResultsIn(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return [.. request.Messages.Where(message => message.ToolResults is not null).SelectMany(message => message.ToolResults!)];
    }

    /// <summary>The names of the tools this request offered the model.</summary>
    /// <param name="request">The request the system issued.</param>
    /// <returns>The offered tool names.</returns>
    public static IReadOnlyList<string> OfferedToolNames(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return [.. (request.Tools ?? []).Select(tool => tool.Name)];
    }

    /// <inheritdoc />
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmCompletion completion = Next(LlmCallKind.Complete, request);

        if (_onCall is { } onCall)
        {
            await onCall();
        }

        return completion;
    }

    /// <inheritdoc />
    public async Task<LlmCompletion> StreamAsync(
        LlmRequest request, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onDelta);

        LlmCompletion completion = Next(LlmCallKind.Stream, request);

        if (_onCall is { } onCall)
        {
            await onCall();
        }

        // Emit the answer as a single delta outside the lock (never await while holding it). The accumulated result
        // matches CompleteAsync, so the caller prices and fail-closes it identically.
        if (completion.Text.Length > 0)
        {
            await onDelta(completion.Text, cancellationToken);
        }

        return completion;
    }

    private LlmCompletion Next(LlmCallKind kind, LlmRequest request)
    {
        Func<LlmRequest, LlmCompletion> step;
        lock (_gate)
        {
            // Recorded BEFORE any throw: "the model was called" is what bounds cost, and the bounded-loop guard needs
            // the attempt counted even when this provider is the thing that refuses it.
            _calls.Add(new RecordedLlmCall(kind, request));

            if (_calls.Count > RunawayThreshold)
            {
                _runawayTripped = true;
                throw new InvalidOperationException(
                    $"gh#930: the chat tool loop made more than {RunawayThreshold} model calls — it is not bounded.");
            }

            if (_script.Count > 0)
            {
                step = _script.Dequeue();
            }
            else if (_always is not null)
            {
                step = _always;
            }
            else
            {
                _unscriptedCalls++;
                throw new InvalidOperationException("gh#930: the turn made a model call the script did not expect.");
            }
        }

        // Invoked outside the lock: a step reads the request and may allocate freely; it must never re-enter this gate.
        return step(request);
    }
}
