using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Bounds how long one <b>non-primary</b> transport can hold the notification pump (gh#1157, ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// The pump is a single reader and <see cref="FanOutNotificationChannel"/> awaits every lane, so a transport that
/// makes several sequential network calls (Discord: webhook, open the DM, post the DM — up to 10 s each) could
/// hold the <i>next</i> queued notification for the sum of them. This wraps the Discord lane, <b>beneath</b> its own
/// dedup lane, so the pump waits for it at most <c>deadline</c>. Pushover is never wrapped: its send is never gated
/// on Discord and never shortened by this bound.
/// </para>
/// <para>
/// Every non-success outcome is reported as <see langword="false"/> — "not accepted" — which the lane's dedup
/// memory treats as not told, so the next re-emission retries <i>that lane only</i>. A timeout cancels the
/// abandoned call through a linked token <b>and</b> stops waiting for it even if it ignores the token; the
/// abandoned task's eventual fault is observed so it can never surface as an unobserved task exception. A
/// non-HTTP exception is absorbed the same way (only its <b>type</b> is logged: its message can carry a webhook
/// URL, which is a credential). A genuine caller cancellation still propagates, so host shutdown stays clean.
/// </para>
/// </remarks>
public sealed class BoundedNotificationChannel : INotificationChannel
{
    private readonly INotificationChannel _inner;
    private readonly string _name;
    private readonly TimeSpan _deadline;
    private readonly ILogger<BoundedNotificationChannel> _logger;

    /// <summary>Creates the bound.</summary>
    /// <param name="inner">The transport to bound.</param>
    /// <param name="name">The transport name, for the log.</param>
    /// <param name="deadline">The total time the pump will wait for one send.</param>
    /// <param name="logger">The logger.</param>
    public BoundedNotificationChannel(
        INotificationChannel inner, string name, TimeSpan deadline, ILogger<BoundedNotificationChannel> logger)
    {
        _inner = inner;
        _name = name;
        _deadline = deadline;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> work;
        try
        {
            work = _inner.SendAsync(notification, linked.Token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Absorb(error);
        }

        try
        {
            return await work.WaitAsync(_deadline, cancellationToken);
        }
        catch (TimeoutException)
        {
            await linked.CancelAsync();

            // The abandoned call may still fault (or be cancelled) later; observe it so it is never unobserved.
            _ = work.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _logger.LogWarning(
                "{Transport} exceeded its {Deadline} s notification deadline; the pump stopped waiting and it will be retried on the next emission.",
                _name, _deadline.TotalSeconds);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown -- not a transport timeout
        }
        catch (OperationCanceledException)
        {
            return Absorb(null, cancelled: true);
        }
        catch (Exception error)
        {
            return Absorb(error);
        }
    }

    /// <inheritdoc />
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) =>
        _inner.ResolveAsync(dedupKey, cancellationToken);

    private bool Absorb(Exception? error, bool cancelled = false)
    {
        // The exception TYPE only: its message or stack can carry the request URI, which for a webhook is a credential.
        _logger.LogWarning(
            "{Transport} notification failed ({ErrorType}); it is not recorded as delivered and will be retried on the next emission.",
            _name, cancelled ? nameof(OperationCanceledException) : error!.GetType().Name);
        return false;
    }
}
