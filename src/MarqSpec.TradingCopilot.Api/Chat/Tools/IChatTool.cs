using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// A tool the chat co-pilot may call to ground its reply in real data (gh#925, chat epic #18 inc 4, R-6). Every tool
/// is <b>read-only by construction</b> — it injects only read seams and reaches no order / execution / write path, so
/// the model can never place, size, or modify an order through the tool layer (enforcement lives below the model).
/// </summary>
/// <remarks>
/// <para>
/// An implementation is <b>owner-scoped (R-20)</b>: it runs under the request's <c>ICurrentUser</c> via the scoped
/// read services it injects, so it can only ever read the operator's own data. It is <b>fail-closed</b>: a malformed
/// input, an unknown argument, or a read fault returns a compact error string the loop wraps as an
/// <see cref="LlmToolResult"/> with <c>IsError = true</c> — it never throws out of <see cref="ExecuteAsync"/> and never
/// invents data.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> returns <b>compact JSON</b> the model reads as the tool result. Money / prices are
/// rendered as their <c>decimal</c> string, never rounded to a float.
/// </para>
/// </remarks>
public interface IChatTool
{
    /// <summary>The tool's stable id — matches <see cref="LlmToolDefinition.Name"/>, echoed on the model's call.</summary>
    string Name { get; }

    /// <summary>The definition offered to the model — its name, the description it reads to decide when to call, and the input schema.</summary>
    LlmToolDefinition Definition { get; }

    /// <summary>
    /// Runs the read and returns a compact-JSON result for the model. Fail-closed: returns an error string rather than
    /// throwing on a malformed <paramref name="inputJson"/> or a read fault; only a genuine caller cancellation propagates.
    /// </summary>
    /// <param name="inputJson">The model-supplied tool input as a JSON string.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A compact-JSON result string.</returns>
    Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken);
}
