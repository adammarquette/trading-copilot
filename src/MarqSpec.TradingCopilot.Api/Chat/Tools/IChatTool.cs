using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// A tool the chat co-pilot may call to ground its reply in real data, or to stage something for the operator (gh#925
/// read set, chat epic #18 inc 4; gh#1059 write set, R-6). Every tool <b>reaches no order, venue, or gate type — by
/// construction</b>: it injects only read seams and the operator's own store, so the model can never place, size, or
/// modify an order through the tool layer (enforcement lives below the model).
/// </summary>
/// <remarks>
/// <para>
/// <b>A write tool is still not an execution path (ADR-0029).</b> The two write tools stage an artifact that is
/// <b>inert until the operator acts</b>: <c>generate_suggestion</c> stages a proposal only the operator can take (the
/// risk gate runs then, below the model), and <c>edit_rulebook</c> writes an <b>Unconfirmed</b> trigger that cannot
/// fire regardless of <c>Enabled</c> until the operator's own confirm arms it. Both are pinned structurally by
/// <c>ChatToolBoundaryTests</c>, which enumerates <b>every</b> implementation of this interface rather than a list
/// somebody has to remember to extend.
/// </para>
/// <para>
/// An implementation is <b>owner-scoped (R-20)</b>: it runs under the request's <c>ICurrentUser</c> via the scoped
/// services it injects, so it can only ever touch the operator's own data. It is <b>fail-closed</b>: a malformed
/// input, an unknown argument, or a read / write fault returns a compact <b>error string</b> the model reads — it
/// never throws out of <see cref="ExecuteAsync"/>, never invents data, and never leaves a partial write.
/// (The loop marks a result <see cref="LlmToolResult.IsError"/> only when it could not dispatch the tool at all — an
/// unknown name, or a tool that threw despite this contract.)
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
    /// Runs the tool and returns a compact-JSON result for the model. Fail-closed: returns an error string rather than
    /// throwing on a malformed <paramref name="inputJson"/> or a read / write fault; only a genuine caller
    /// cancellation propagates.
    /// </summary>
    /// <param name="inputJson">The model-supplied tool input as a JSON string.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A compact-JSON result string.</returns>
    Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken);
}
