using System.Collections.Concurrent;
using MarqSpec.TradingCopilot.Domain.Notifications;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An in-process <see cref="INotificationChannel"/> that <b>records</b> instead of sending (gh#246).
/// </summary>
/// <remarks>
/// <para>
/// The issue is explicit that <b>no test may ever send a real push</b> — a suite that pages the operator is a
/// suite nobody will run. This is the sanctioned double for that reason, and it is deliberately *inert*: it
/// records what it was asked to do and computes nothing, so a test asserting "one page, Emergency priority"
/// cannot be satisfied by the double handing back the answer.
/// </para>
/// <para>
/// It also models the two ways a channel can misbehave — <see cref="MakeSendThrow"/> and
/// <see cref="MakeSendHang"/> — because the property that matters most is that <b>neither can affect the
/// flatten</b>. Alerting is reporting; it must never be able to break trading (engineering §9).
/// </para>
/// </remarks>
public sealed class RecordingNotificationChannel : INotificationChannel
{
    private readonly ConcurrentQueue<Notification> _sent = new();
    private readonly ConcurrentQueue<string> _resolved = new();
    private bool _throwOnSend;
    private TimeSpan _sendDelay = TimeSpan.Zero;

    /// <summary>Every notification the system asked to send, in order.</summary>
    public IReadOnlyList<Notification> Sent => [.. _sent];

    /// <summary>Every dedup key the system asked to resolve, in order.</summary>
    public IReadOnlyList<string> Resolved => [.. _resolved];

    /// <summary>Notifications at <see cref="NotificationSeverity.Page"/> — the ones that reach a phone.</summary>
    public IReadOnlyList<Notification> Pages =>
        [.. _sent.Where(notification => notification.Severity == NotificationSeverity.Page)];

    /// <summary>Makes every send throw — the channel is down, and the flatten must not care.</summary>
    public void MakeSendThrow() => _throwOnSend = true;

    /// <summary>Makes every send hang for <paramref name="delay"/> — a slow channel must not delay a flatten.</summary>
    /// <param name="delay">How long each send should block.</param>
    public void MakeSendHang(TimeSpan delay) => _sendDelay = delay;

    /// <summary>Clears recordings and failure modes — call at the start of each test.</summary>
    public void Reset()
    {
        _sent.Clear();
        _resolved.Clear();
        _throwOnSend = false;
        _sendDelay = TimeSpan.Zero;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _sent.Enqueue(notification);

        if (_sendDelay > TimeSpan.Zero)
        {
            await Task.Delay(_sendDelay, cancellationToken);
        }

        return _throwOnSend
            ? throw new InvalidOperationException("Notification channel unreachable (test double).")
            : true;
    }

    /// <inheritdoc />
    // Signature follows gh#300's INotificationChannel change. True: this recorder always "cancels" successfully,
    // so the pump's cancel-retry never fires and existing alerting assertions are unaffected.
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        _resolved.Enqueue(dedupKey);
        return Task.FromResult(true);
    }
}
