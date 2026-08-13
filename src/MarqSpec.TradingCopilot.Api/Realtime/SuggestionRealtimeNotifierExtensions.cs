using MarqSpec.TradingCopilot.Api.Suggestions;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// Pushes the rows a set-based lifecycle transition changed to their owners (gh#718) — the drift → <c>Stale</c> and
/// expire → <c>ExpiredVoid</c> half of the suggestion realtime push, the counterpart to the issued / superseded half
/// <c>TriggerEvaluationService</c> queues (gh#684).
/// </summary>
internal static class SuggestionRealtimeNotifierExtensions
{
    /// <summary>
    /// Pushes each transitioned suggestion's new lifecycle <paramref name="state"/> to its owner, <b>best-effort</b>:
    /// a hub fault on one push is logged and swallowed so it can never fail or roll back the state write that already
    /// committed (ADR-0021, the same posture as <c>TriggerEvaluationService.NotifySafelyAsync</c>). Routed per-owner
    /// by <see cref="ISuggestionRealtimeNotifier.SuggestionChangedAsync"/> (R-20 — never broadcast). Only the caller's
    /// own cancellation escapes.
    /// </summary>
    /// <param name="notifier">The per-owner realtime push seam (gh#684).</param>
    /// <param name="transitions">The rows the transition changed, each with its id and owner.</param>
    /// <param name="state">The new lifecycle state's name (e.g. <c>Stale</c> / <c>ExpiredVoid</c>) — the wire payload.</param>
    /// <param name="at">When the transition committed — carried on the payload.</param>
    /// <param name="logger">The caller's logger, for a swallowed hub fault.</param>
    /// <param name="cancellationToken">The caller's cancellation token; only its cancellation escapes.</param>
    public static async Task PushTransitionsSafelyAsync(
        this ISuggestionRealtimeNotifier notifier,
        IReadOnlyList<SuggestionTransition> transitions,
        string state,
        DateTimeOffset at,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (SuggestionTransition transition in transitions)
        {
            try
            {
                await notifier.SuggestionChangedAsync(
                    transition.UserId,
                    new RealtimeSuggestion(transition.SuggestionId, state, at),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogError(
                    error,
                    "Realtime suggestion push for {SuggestionId} ({State}) failed for owner {Owner}; the state write is committed regardless.",
                    transition.SuggestionId, state, transition.UserId);
            }
        }
    }
}
