using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Chat;

/// <summary>
/// The result of one grounded chat turn (gh#906): whether it produced a usable answer, the text to surface (the
/// assistant answer, or a refusal reason), and the priced <see cref="AiCallCost"/> the caller meters and ledgers.
/// </summary>
/// <param name="Succeeded">
/// <see langword="true"/> only on a clean completion — the caller then persists <see cref="Message"/> as the
/// assistant turn. <see langword="false"/> for a refused / truncated / faulted call (fail-closed): the caller
/// surfaces <see cref="Message"/> as a refusal and persists <b>no</b> assistant turn.
/// </param>
/// <param name="Message">The assistant answer (on success) or a human refusal reason (on failure).</param>
/// <param name="Cost">The call's priced facts — recorded whatever the outcome, so a failed call still reaches the governor.</param>
public sealed record ChatTurnResult(bool Succeeded, string Message, AiCallCost Cost);

/// <summary>
/// Runs one grounded co-pilot chat turn over <see cref="ILlmProvider"/> (gh#906, R-6): builds the model conversation
/// from the thread's history under a fixed system prompt, calls the model, and prices the call. <b>Pure of
/// persistence, tenancy, the hub, and the governor</b> — the endpoint owns those — so it is trivially unit-testable
/// behind a fake provider (the reviewer / scan split).
/// </summary>
/// <remarks>
/// <b>Enforcement lives below the model.</b> Nothing here places, sizes, or proposes an order — the co-pilot only
/// converses. The system prompt is <b>fixed and holds no risk limits or account state</b>, and message
/// <see cref="ChatMessage.Content"/> is <b>untrusted display data</b>: it becomes a User / Assistant turn, never
/// folded into the system prompt as instruction. Any stop other than a clean completion is treated <b>fail-closed</b>
/// (the <see cref="ILlmProvider"/> seam contract): the model's text is not surfaced as the co-pilot's answer.
/// </remarks>
public interface IChatTurnService
{
    /// <summary>
    /// Runs one turn over the conversation history (oldest first), <b>streaming</b> the answer token-by-token (inc 3b).
    /// <paramref name="onDelta"/> is invoked for each text delta as it arrives; the returned result carries the full
    /// answer (or refusal) and the priced cost, so a caller that does not want streaming passes a no-op delta and gets
    /// exactly the non-streamed behaviour.
    /// </summary>
    /// <param name="history">The thread's messages in <c>Sequence</c> order — must end with the operator's new turn.</param>
    /// <param name="onDelta">Called with each incremental text delta (a presentation side-channel; should not throw).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The turn result — the answer or a refusal, and the priced cost.</returns>
    Task<ChatTurnResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class ChatTurnService : IChatTurnService
{
    /// <summary>The tier a chat turn runs at — genuine synthesis, so the deep model (ADR-0008), capped by the governor.</summary>
    private const LlmModelTier Tier = LlmModelTier.Deep;

    /// <summary>A hard output ceiling for one turn; a longer answer truncates (and fails closed) rather than running away.</summary>
    private const int MaxOutputTokens = 1024;

    /// <summary>
    /// The co-pilot system prompt. <b>Fixed</b> — never assembled from message <c>Content</c> — and holds <b>no risk
    /// limits or account state</b> (enforcement lives below the model; the LLM only proposes).
    /// </summary>
    private const string SystemPrompt =
        "You are a trading co-pilot for a single futures trader. Help them analyse markets, setups, order flow, and "
        + "their own trading, grounded in the conversation. You never place, modify, or size orders, and you never "
        + "give personalized financial advice — execution is always an explicit action the trader takes themselves. "
        + "Be concise and specific, and say plainly when you are not sure.";

    private readonly ILlmProvider _provider;
    private readonly LlmOptions _options;
    private readonly ILogger<ChatTurnService> _logger;

    /// <summary>Creates the service over the provider seam and the pricing options.</summary>
    /// <param name="provider">The provider-neutral LLM seam.</param>
    /// <param name="options">The LLM options — the model id and pinned per-tier rates.</param>
    /// <param name="logger">The logger (a faulted call is logged, never silently swallowed).</param>
    public ChatTurnService(ILlmProvider provider, IOptions<LlmOptions> options, ILogger<ChatTurnService> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChatTurnResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(onDelta);

        // Only User / Assistant turns become the model conversation; a System or Unknown row is not a turn. Each turn's
        // Content rides as a User or prior-Assistant message, never elevated into the system prompt (R-6).
        List<LlmMessage> messages =
        [
            .. history
                .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
                .Select(message => new LlmMessage(
                    message.Role == ChatRole.Assistant ? LlmRole.Assistant : LlmRole.User, message.Content))
        ];

        LlmRequest request = new(Tier, SystemPrompt, messages, LlmResponseFormat.Text, MaxOutputTokens);
        string model = _options.ModelFor(Tier);

        long start = Stopwatch.GetTimestamp();
        LlmCompletion completion;
        try
        {
            // Streaming: each text delta is forwarded to onDelta as it arrives; the returned completion is the full
            // accumulated answer, so pricing and the fail-closed check below are identical to the non-streamed path.
            completion = await _provider.StreamAsync(request, onDelta, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller's own cancellation, not a provider fault
        }
        catch (Exception error)
        {
            // A provider fault (transport / timeout / 5xx) — billable latency that produced nothing (gh#431). Fail
            // CLOSED: invent no assistant text; the cost still records the failed, zero-token call for the governor.
            _logger.LogWarning(error, "Chat turn LLM call faulted; failing closed.");
            return new ChatTurnResult(
                false,
                "The co-pilot could not complete a response right now. Your message was saved — please try again.",
                new AiCallCost(AiUsageFeature.Chat, model, Tier, AiUsageOutcome.Failed, 0, 0, 0m, Stopwatch.GetElapsedTime(start)));
        }

        TimeSpan latency = Stopwatch.GetElapsedTime(start);
        AiUsageOutcome outcome = completion.StopReason switch
        {
            LlmStopReason.Completed => AiUsageOutcome.Succeeded,
            LlmStopReason.MaxTokens => AiUsageOutcome.Truncated,
            LlmStopReason.Refusal => AiUsageOutcome.Refused,
            _ => AiUsageOutcome.Failed,
        };
        AiCallCost cost = new(
            AiUsageFeature.Chat,
            model,
            Tier,
            outcome,
            completion.Usage.InputTokens,
            completion.Usage.OutputTokens,
            _options.EstimateCost(Tier, completion.Usage.InputTokens, completion.Usage.OutputTokens),
            latency);

        // Fail CLOSED on anything but a clean completion (the ILlmProvider seam contract): a refused / truncated /
        // other stop is not an answer the operator should read as the co-pilot's, so it is surfaced as a refusal and
        // never persisted as an assistant turn — while the call is still metered and ledgered above.
        return completion.StopReason == LlmStopReason.Completed
            ? new ChatTurnResult(true, completion.Text, cost)
            : new ChatTurnResult(false, RefusalFor(completion.StopReason), cost);
    }

    private static string RefusalFor(LlmStopReason stopReason) => stopReason switch
    {
        LlmStopReason.Refusal => "The co-pilot declined to answer that.",
        LlmStopReason.MaxTokens => "The co-pilot's response was too long to finish — try narrowing the question.",
        _ => "The co-pilot could not complete a response right now. Please try again.",
    };
}
