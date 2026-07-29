using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An <b>adversarial</b> stand-in for the LLM provider (gh#429, gh#402). Like the venue, this is an outbound
/// third-party seam that cannot exist pre-merge — but it is deliberately placed at the <b>provider</b>, not the
/// reviewer: the real <c>LlmTriggerReviewer</c> stays under test, so its fail-closed parsing is exercised rather
/// than stubbed away.
/// </summary>
/// <remarks>
/// It <b>feeds completions and never the decision</b>. A test hands it raw model text — including hostile text that
/// asks for execution or an oversized position — and the system must still compute the outcome itself. It also
/// <b>records every request</b>, so a suite can prove what did (and did not) reach the model: the review context
/// carries market facts only, never an account, size, or mode.
/// </remarks>
public sealed class AdversarialLlmProvider : ILlmProvider
{
    private readonly List<LlmRequest> _requests = [];
    private readonly Lock _gate = new();
    private string _text = """{"decision":"suppress","reason":"default"}""";
    private LlmStopReason _stopReason = LlmStopReason.Completed;
    private bool _throws;

    /// <summary>Every request the system sent to the model, in call order.</summary>
    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The number of times the model was called — the LLM-cost meter a debounce guard asserts on.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>Makes the next completions return <paramref name="text"/> verbatim (raw model output, not a decision).</summary>
    public void Returns(string text, LlmStopReason stopReason = LlmStopReason.Completed)
    {
        lock (_gate)
        {
            _text = text;
            _stopReason = stopReason;
            _throws = false;
        }
    }

    /// <summary>A well-formed proposal. The prices are the model's; size, mode and account are deliberately absent.</summary>
    public void ReturnsSuggestion(string direction, decimal entry, decimal stop, decimal target) =>
        Returns($$"""
            {"decision":"suggest","direction":"{{direction}}","entry":{{entry}},"stop":{{stop}},"target":{{target}},"reason":"test"}
            """);

    /// <summary>Makes the provider throw — the network / timeout / 429 / 5xx case the real client raises.</summary>
    public void MakeThrow()
    {
        lock (_gate)
        {
            _throws = true;
        }
    }

    /// <summary>Clears recorded requests and restores the default (suppress) completion — call per test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _requests.Clear();
            _text = """{"decision":"suppress","reason":"default"}""";
            _stopReason = LlmStopReason.Completed;
            _throws = false;
        }
    }

    /// <inheritdoc />
    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Recorded BEFORE any throw: "the provider was called" is what bounds LLM cost, and a suite proving a
            // fault does not fan out needs the attempt counted even when it fails.
            _requests.Add(request);
            if (_throws)
            {
                throw new InvalidOperationException("the review provider is unavailable (test)");
            }

            return Task.FromResult(new LlmCompletion(_text, _stopReason, LlmUsage.None));
        }
    }
}
