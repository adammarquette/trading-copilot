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
    private readonly Dictionary<LlmModelTier, string> _textByTier = [];
    private string _text = """{"decision":"suppress","reason":"default"}""";
    private LlmStopReason _stopReason = LlmStopReason.Completed;
    private LlmUsage _usage = LlmUsage.None;
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

    /// <summary>
    /// Makes every completion report <paramref name="usage"/> (gh#559), so a fire prices at a <b>non-zero</b> cost
    /// and the <c>AiUsage</c> ledger assertions built on it mean something.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="LlmUsage.None"/>, which prices every call at <b>$0.00</b> — so an unconfigured suite
    /// asserting on cost, on a budget bound, or on an escalation being withheld is satisfied by zero <i>whether or
    /// not production works</i>. That is a vacuous guard, and an invisible one: nothing about a passing spend suite
    /// announces that every number in it was zero. Call this before asserting on spend.
    /// </remarks>
    /// <param name="usage">The token counts each completion reports.</param>
    public void ReportsUsage(LlmUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            _usage = usage;
        }
    }

    /// <summary>
    /// Scripts a completion for one <b>tier</b> (gh#559), so a triage→deep escalation can return different text for
    /// its two calls (gh#449) instead of the same body twice.
    /// </summary>
    /// <param name="tier">The tier this text answers.</param>
    /// <param name="text">Raw model output for that tier.</param>
    public void ReturnsForTier(LlmModelTier tier, string text)
    {
        lock (_gate)
        {
            _textByTier[tier] = text;
            _throws = false;
        }
    }

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
            _textByTier.Clear();
            _usage = LlmUsage.None;
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

            // A tier-scripted body wins over the flat one, so an escalation's two calls can differ (gh#449); the
            // flat text remains the default, so every existing suite is untouched.
            string body = _textByTier.TryGetValue(request.Tier, out string? scripted) ? scripted : _text;
            return Task.FromResult(new LlmCompletion(body, _stopReason, _usage));
        }
    }
}
