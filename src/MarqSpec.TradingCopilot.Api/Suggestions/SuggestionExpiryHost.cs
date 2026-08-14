using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// The suggestion <b>expire sweep</b> (gh#545, ADR-0013): a periodic background pass that voids every live
/// suggestion past its validity window, so a dead suggestion drops off the actionable surface without an operator
/// act. <b>Time-driven</b>, not event-driven — a suggestion expires on the clock, not on a quote — so unlike the
/// stop-promotion / conditional-firing consumers it needs no event-log cursor.
/// </summary>
/// <remarks>
/// A <b>fresh DI scope per pass</b> keeps host shutdown clean (gh#169); the transition reads across the R-20 boundary
/// deliberately (background plumbing acting for the deployment, not a request user). The clock is read <b>here</b>,
/// at the boundary, and passed in, so the decision stays pure (ADR-0013 / R-12). The sweep is <b>idempotent</b> — a
/// pass that fails or is redelivered voids only what is still due — and the <b>first pass after a restart is the
/// recovery catch-up</b>: the startup path (<c>StartupTasks</c>) runs the same transition before the rehydrator
/// counts, so there is no separate resurrection code (ADR-0013's "expire + re-validate, never auto-resume").
/// </remarks>
public sealed class SuggestionExpiryHost : BackgroundService
{
    /// <summary>
    /// How often the sweep runs. A suggestion's validity window is minute-scale, and the take path re-checks validity
    /// <b>synchronously</b> (R-12) so a just-expired-but-unswept suggestion is still blocked at the gate — a
    /// sub-second cadence would buy nothing. This keeps the read surface fresh without hot-looping the database.
    /// </summary>
    private static TimeSpan SweepInterval { get; } = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<SuggestionExpiryHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="logger">The logger.</param>
    public SuggestionExpiryHost(IServiceProvider services, ILogger<SuggestionExpiryHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A FRESH scope per pass: the database is scoped, so holding one for the app's lifetime would leak
                // tracked entities and, on host teardown, leave this loop acting on disposed services (gh#169).
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    await ExpireAndNotifyAsync(
                        scope.ServiceProvider.GetRequiredService<ISuggestionExpiry>(),
                        scope.ServiceProvider.GetRequiredService<ISuggestionRealtimeNotifier>(),
                        DateTimeOffset.UtcNow,
                        _logger,
                        stoppingToken);
                }

                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // A transient DB fault must not kill the sweep: log, back off, retry. The sweep is idempotent, so a
                // failed pass loses nothing -- the next voids whatever is still due.
                _logger.LogWarning(error, "Suggestion expire sweep failed; retrying after {Delay}.", SweepInterval);
                try
                {
                    await Task.Delay(SweepInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Runs one expire sweep and pushes each voided suggestion to its owner (gh#718), returning how many were voided.
    /// Extracted from the loop and <see langword="internal"/> <b>only</b> so the recovery-and-route behaviour can be
    /// unit-tested against fakes without spinning the background host — the push is the part gh#718 singled out as the
    /// harder shape (no id or owner in hand until <see cref="ISuggestionExpiry.ExpireDueAsync"/> recovers it) and the
    /// one whose failure is silent: a wrong owner, state string, or a shadowed count guard leaves a dead card on
    /// screen with nothing red.
    /// </summary>
    /// <param name="expiry">The guarded expire transition, which now returns the voided rows (gh#718).</param>
    /// <param name="notifier">The per-owner realtime push seam (gh#684).</param>
    /// <param name="now">The moment the sweep runs as of — supplied by the caller; the decision never reads a clock.</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many suggestions this sweep voided.</returns>
    internal static async Task<int> ExpireAndNotifyAsync(
        ISuggestionExpiry expiry,
        ISuggestionRealtimeNotifier notifier,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SuggestionTransition> expired = await expiry.ExpireDueAsync(now, cancellationToken);
        if (expired.Count == 0)
        {
            return 0;
        }

        logger.LogInformation(
            "Suggestion expire sweep voided {Count} past-window suggestion(s) (gh#545).", expired.Count);

        // gh#718: the expire write has committed, so push each voided suggestion to its owner (R-20, Clients.User) so
        // its card clears without a poll -- the drift/expiry half of the gh#684 realtime push. Best-effort: a hub
        // fault is logged and swallowed, never affecting the write (ADR-0021).
        await notifier.PushTransitionsSafelyAsync(
            expired, SuggestionState.ExpiredVoid.ToString(), now, logger, cancellationToken);
        return expired.Count;
    }
}
