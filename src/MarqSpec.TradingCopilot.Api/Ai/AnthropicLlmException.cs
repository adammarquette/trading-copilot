namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// A failure to obtain a usable completion from the Anthropic provider (A2, gh#423) — a non-2xx status, or a 2xx
/// whose body is not the shape the Messages API promises.
/// </summary>
/// <remarks>
/// It is deliberately an exception, not a sentinel completion: the reviewer's fail-closed catch maps it to
/// <c>Suppress(ReviewerUnavailable)</c> (gh#402), so a provider failure becomes a suppression — never a fabricated
/// suggestion. A genuine caller cancellation is <b>not</b> wrapped in this; it propagates as
/// <see cref="OperationCanceledException"/> so host shutdown stays clean.
/// </remarks>
public sealed class AnthropicLlmException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message safe to log — it carries no key and no response body.</param>
    public AnthropicLlmException(string message)
        : base(message)
    {
    }
}
