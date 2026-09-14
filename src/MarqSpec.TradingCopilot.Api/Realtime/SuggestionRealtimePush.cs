using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// The <b>one</b> owner of the best-effort per-owner push policy for a set-based suggestion transition (gh#718): the
/// drift watcher and the expire sweep both recover the <c>(SuggestionId, UserId)</c> rows they moved and push a
/// signal for each, and this keeps "log, swallow, keep going, and treat cancellation differently from a fault" a
/// single rule so the two call sites cannot drift apart the first time the logging or the catch is adjusted.
/// </summary>
internal static class SuggestionRealtimePush
{
    /// <summary>
    /// Pushes one <see cref="RealtimeSuggestion"/> per transitioned row to its owner (<c>Clients.User</c>, R-20 —
    /// never a broadcast), stamped with <paramref name="state"/> and <paramref name="now"/>. <b>Best-effort and after
    /// the write has committed</b>: the transition is already durable, so a hub fault on one row only logs and is
    /// swallowed — it must never fail the pass or unwind the write, and it must not stop the remaining rows' pushes.
    /// A caller cancellation propagates (it is a clean stop, not a delivery fault).
    /// </summary>
    public static async Task PushTransitionsSafelyAsync(
        this ISuggestionRealtimeNotifier notifier,
        IReadOnlyList<SuggestionTransition> transitions,
        SuggestionState state,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (SuggestionTransition transition in transitions)
        {
            try
            {
                await notifier.SuggestionChangedAsync(
                    transition.UserId,
                    new RealtimeSuggestion(transition.SuggestionId, state.ToString(), now),
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
                    "Realtime suggestion push for {SuggestionId} ({State}) failed for owner {Owner}; the transition is committed regardless.",
                    transition.SuggestionId, state, transition.UserId);
            }
        }
    }
}
