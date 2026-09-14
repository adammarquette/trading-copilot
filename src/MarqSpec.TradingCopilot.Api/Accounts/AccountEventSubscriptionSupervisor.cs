using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// Keeps the account-event subscription alive across drops (R-17, gh#219), with the same drop-vs-stop discipline
/// as <c>QuoteSubscriptionSupervisor</c>: a session that <b>ends or throws</b> is a <b>drop</b> — log, back off,
/// re-subscribe; <b>cancellation</b> is a <b>stop</b> — propagate, no backoff. Teardown exceptions
/// (<see cref="ObjectDisposedException"/>) and an unsupported-capability refusal are not transient drops, so they
/// propagate to the host to exit cleanly rather than spin.
/// </summary>
public sealed class AccountEventSubscriptionSupervisor
{
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<AccountEventSubscriptionSupervisor> _logger;

    /// <summary>Creates the supervisor.</summary>
    /// <param name="reconnectDelay">How long to wait before re-subscribing after a drop.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The delay function — injectable so a test need not wait real time.</param>
    public AccountEventSubscriptionSupervisor(
        TimeSpan reconnectDelay,
        ILogger<AccountEventSubscriptionSupervisor> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _reconnectDelay = reconnectDelay;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Runs <paramref name="session"/> to completion, re-running it after each drop until cancelled.</summary>
    /// <param name="session">One subscribe-and-consume session; returns on a clean stream end, throws on a fault.</param>
    /// <param name="cancellationToken">Cancels the whole supervision (a stop).</param>
    /// <returns>A task that completes only when cancelled (by throwing the cancellation).</returns>
    public async Task RunAsync(Func<CancellationToken, Task> session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        while (true)
        {
            try
            {
                await session(cancellationToken);
                _logger.LogInformation("Account-event stream ended; re-subscribing after {Delay}.", _reconnectDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // a stop -- propagate, no backoff
            }
            catch (ObjectDisposedException)
            {
                throw; // teardown (the root provider is going away), not a transient drop -- let the host exit clean
            }
            catch (VenueCapabilityNotSupportedException)
            {
                throw; // the venue cannot stream account events at all -- not a drop to retry (R-17)
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "Account-event stream dropped; re-subscribing after {Delay}.", _reconnectDelay);
            }

            // A stop requested mid-session (even one that then ended cleanly) exits before paying the backoff.
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(_reconnectDelay, cancellationToken);
        }
    }
}
