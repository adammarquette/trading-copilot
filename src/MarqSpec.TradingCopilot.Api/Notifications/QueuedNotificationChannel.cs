using System.Threading.Channels;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Accepts notifications without blocking the caller and delivers them on a background pump (gh#289, fixing
/// gh#243) — the decorator that keeps the send <b>off the flatten hot path</b>.
/// </summary>
/// <remarks>
/// <para>
/// gh#243 asserted that sending was off the hot path and it was not: the flatten awaited the send inline, so a
/// slow channel added its full latency to a pass on the <b>R-13 safety path</b> — and it did so precisely when a
/// position was already failing to close. gh#246 measured a 5-second channel turning a pass into 5.15 seconds.
/// </para>
/// <para>
/// Queueing rather than a bare fire-and-forget, for three reasons the issue called out:
/// </para>
/// <list type="bullet">
/// <item><b>Dedup stops racing itself.</b> The pump is single-threaded, so the "have I already reported this
/// incident?" check and the record of having reported it can no longer interleave — two escalations arriving
/// together used to be able to both pass the check and double-page.</item>
/// <item><b>A failed delivery is still a failed delivery.</b> The pump sees the real result, so
/// <see cref="DedupingNotificationChannel"/> below it still refuses to record an incident it did not manage to
/// report. A detached task whose result nobody read would have lost that.</item>
/// <item><b>Shutdown drains instead of dropping.</b> A page queued as the host stops is the one most worth
/// delivering — see <see cref="DrainPendingAsync"/>.</item>
/// </list>
/// <para>
/// <b>The contract shifts subtly and deliberately:</b> <see cref="SendAsync"/> returning <see langword="true"/>
/// now means <i>accepted for delivery</i>, not <i>delivered</i>. Callers on the safety path cannot wait for
/// delivery without reintroducing the defect, so the honest thing is to say so rather than to imply a guarantee
/// the design gives up on purpose.
/// </para>
/// <para>
/// <b>A full queue REFUSES; it never drops (gh#1077).</b> This class was written with
/// <c>BoundedChannelFullMode.DropWrite</c> and a comment promising that "a drop is logged". Under <i>any</i>
/// <c>Drop*</c> mode <c>TryWrite</c> discards the item and returns <see langword="true"/>, so both drop branches
/// were unreachable: nothing was logged and every caller was told the notification landed. The mode is
/// <see cref="BoundedChannelFullMode.Wait"/> now — used with <c>TryWrite</c> and never
/// <c>WaitToWriteAsync</c>, so nothing ever blocks — because it is the only mode under which <c>TryWrite</c>
/// reports a full queue at all.
/// </para>
/// <para>
/// <b>Refusing is the right policy for this payload, and the alternatives are not equivalent.</b>
/// </para>
/// <list type="bullet">
/// <item><b>Blocking with a bounded wait</b> puts the transport's backpressure onto the caller, and the caller is
/// the auto-flatten on the R-13 path. That is gh#289 re-introduced as a smaller number.</item>
/// <item><b><c>DropOldest</c></b> leaves <c>TryWrite</c>'s answer just as dishonest, and discards the wrong end:
/// the <i>first</i> page of an incident is the one that matters, and the later ones are repeats the dedup layer
/// below would collapse anyway.</item>
/// <item><b>Refusing</b> is the only outcome that is <i>recoverable</i>. The layer above is the durable outbox,
/// and <c>NotificationOutboxRelay</c> already reads <see langword="false"/> as "the row stays owed and is retried
/// next pass". Refusing hands the page back to the ledger that remembers it instead of destroying it.</item>
/// </list>
/// <para>
/// <b>Who actually receives a refusal — traced, because the producers are three layers up.</b> Every producer
/// holds <c>OutboxNotificationChannel</c>, not this class, and the two verbs arrive here by different routes. A
/// <b>send</b> never travels from a producer to this queue: the outbox seam writes a row and returns (returning
/// <see langword="true"/> for an already-owed row as well), so the send reaching <see cref="SendAsync"/> comes
/// from the relay — which is the right recipient, since it owns the ledger the refusal hands the page back to.
/// The <b>operator</b> learns of it from the metric and its P1 rule, not from a return value nobody upstream can
/// act on. A <b>resolve</b> is the opposite: <c>OutboxNotificationChannel.ResolveAsync</c> returns this queue's
/// answer verbatim, so both the <see langword="false"/> and the out-of-band key release land at the caller.
/// </para>
/// <para>
/// <b>Three different <see langword="true"/>s already live in this chain, and nothing here adds a fourth.</b> The
/// outbox seam's means <i>durably recorded</i>; this class's means <i>accepted for delivery</i>; only the
/// transport's means <i>delivered</i>. The defect was returning the middle one when nothing had been accepted.
/// Do not read any of the three as the one below it.
/// </para>
/// <para>
/// <b>Pages and resolves are budgeted separately, because their losses are not comparable.</b> A refused page is
/// recoverable, as above. A refused <i>resolve</i> is not: nothing above this queue records that a resolve is
/// owed, and a producer that resolves once per outage — <c>TriggerEvaluationService</c>'s staleness recovery —
/// never comes back for it. So a send is refused at <see cref="PageCapacity"/> while the channel keeps
/// <see cref="ResolveHeadroom"/> slots beyond it that only a resolve, or the pump's own cancel-retry, can reach.
/// What fills this queue is <i>pages</i> — the escalation re-emitting every 15 s against a transport that is not
/// draining — so without the reserve the resolve is precisely the item crowded out. And if even the reserve is
/// exhausted, the dedup key is released out of band through <see cref="IIncidentKeyRegistry"/> (see
/// <see cref="ResolveAsync"/>), so <b>no outcome of this queue can leave a key held for the life of the
/// process</b>.
/// </para>
/// </remarks>
public sealed class QueuedNotificationChannel : INotificationChannel
{
    /// <summary>
    /// How many times the pump will try to cancel an outstanding page before giving up (gh#300).
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. A receipt Pushover permanently refuses to cancel would otherwise spin forever on this
    /// single-reader pump and starve every delivery queued behind it — including a page for a *live* incident,
    /// which is far worse than the stale nag being retried. Three attempts covers a transient fault or timeout;
    /// past that the page is left to expire on its own and the give-up is logged.
    /// </remarks>
    public const int MaxResolveAttempts = 3;

    /// <summary>How deep the queue may get before a <b>page</b> is refused (gh#1077).</summary>
    /// <remarks>
    /// The original bound, unchanged: a wedged transport must not grow this queue without limit beside a trading
    /// process. It is a soft cap on total depth now rather than the channel's capacity, so the slots above it stay
    /// available to a resolve — see <see cref="ResolveHeadroom"/>.
    /// </remarks>
    public const int PageCapacity = 256;

    /// <summary>Slots beyond <see cref="PageCapacity"/> only a resolve or a cancel-retry can reach (gh#1077).</summary>
    /// <remarks>
    /// Sized against what can be <i>owed</i> rather than against throughput: a resolve exists per <b>open
    /// incident</b>, and the set of things that can concurrently be wrong is a handful of account/instrument pairs
    /// plus a trigger or two — the same bound that lets <c>DedupingNotificationChannel</c> hold its incident set
    /// with no eviction policy. Sixty-four is generous against that and small beside the page budget it protects.
    /// </remarks>
    public const int ResolveHeadroom = 64;

    private readonly INotificationChannel _inner;
    private readonly IIncidentKeyRegistry _incidents;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<QueuedNotificationChannel> _logger;
    private readonly Channel<Delivery> _queue;

    /// <summary>Creates the queueing decorator.</summary>
    /// <param name="inner">The channel that actually delivers — deduping, then the transport.</param>
    /// <param name="incidents">
    /// Releases a dedup key without the queue, for the one case the queue cannot carry a resolve. <b>Required, not
    /// optional</b>: a dependency that defaulted to a no-op would make the gh#1077 failure silent again, and
    /// silently is exactly how it got here.
    /// </param>
    /// <param name="metrics">Meters a refusal, so Layer 2 can see an alerting path that is failing to alert.</param>
    /// <param name="logger">The logger.</param>
    public QueuedNotificationChannel(
        INotificationChannel inner,
        IIncidentKeyRegistry incidents,
        IExecutionMetrics metrics,
        ILogger<QueuedNotificationChannel> logger)
    {
        _inner = inner;
        _incidents = incidents;
        _metrics = metrics;
        _logger = logger;

        // Bounded, so a wedged transport cannot grow the queue without limit beside a trading process. WAIT rather
        // than any Drop* mode, because Drop* is what made this class lie: TryWrite discards the item and returns
        // true under every Drop mode, so the "queue is full" branches below could never run (gh#1077). Nothing here
        // ever calls WaitToWriteAsync, so Wait never blocks a caller -- it only makes TryWrite tell the truth.
        _queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(PageCapacity + ResolveHeadroom)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Enqueues and returns; it never waits for the transport. See the type remarks. Returns
    /// <see langword="false"/> — loudly — once the queue is at <see cref="PageCapacity"/>: the page is
    /// <b>refused, not dropped</b>, so the durable outbox above keeps owing it and the next relay pass re-offers
    /// it.
    /// </remarks>
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The soft cap is read BEFORE the write, so pages stop at PageCapacity and the slots above it stay for a
        // resolve. Two concurrent sends can both read a depth just under the cap and both write; the reserve
        // absorbs that, which is the other reason it is generous rather than exact.
        if (_queue.Reader.Count >= PageCapacity || !_queue.Writer.TryWrite(Delivery.Send(notification)))
        {
            // ERROR, deliberately, and never Debug: appsettings.json sets Logging:LogLevel:Default to Information,
            // so anything below that is never written in production -- a "logged" drop nobody can read is the
            // defect being fixed, not the fix. Metered as well (gh#1077), because a log line is visible to an
            // engineer who goes looking and the operator is exactly who is not being told.
            _logger.LogError(
                "Notification queue is full — REFUSED a {Severity} for {Incident}; the transport is not draining. "
                + "The page is not lost: it stays owed in the outbox and is re-offered on the next relay pass.",
                notification.Severity, notification.DedupKey);
            _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedPage);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Enqueued like a send, so ordering with the send it clears is preserved — and enqueued into the reserve
    /// <see cref="ResolveHeadroom"/> keeps for it, so a queue full of pages cannot crowd it out. In the residual
    /// case where even that is exhausted the dedup key is released <b>out of band</b> before returning
    /// <see langword="false"/>: the cancel is recoverable by a retrying caller, the release is recoverable by
    /// nobody.
    /// </remarks>
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        if (!_queue.Writer.TryWrite(Delivery.Resolve(dedupKey)))
        {
            // THE BRACE (gh#1077). A resolve carries two halves that fail differently. Cancelling the outstanding
            // page needs the transport, so it is lost here and `false` asks the caller to try again -- which is
            // INotificationChannel's documented meaning for `false`. Releasing the dedup key needs nothing but a
            // dictionary removal, and NOTHING anywhere remembers that it is owed: there is no outbox row for a
            // resolve, and a producer that resolves once per outage never comes back. Left held, the key
            // suppresses every later incident on it for the life of the process -- ADR-0019's "one notification
            // per process lifetime instead of one per outage", reached without any producer doing anything wrong
            // (gh#1045, gh#1051).
            //
            // So the release happens here, on the caller's thread, and ONLY here: doing it on the ordinary path
            // would let a concurrent escalation on the same key slip past the suppression it is meant to meet.
            // The cost of releasing is a duplicate page, which is the direction DedupingNotificationChannel
            // already chose for its own unconditional re-arm.
            bool released = _incidents.ReleaseIncident(dedupKey);

            _logger.LogCritical(
                "Notification queue is full — REFUSED the resolve for {Incident}; the transport is not draining. "
                + "Its dedup key was released out of band ({Released}), so a later occurrence of this incident is "
                + "still reported — but any page already raised for it keeps nagging until it expires or is "
                + "acknowledged.",
                dedupKey, released ? "a repeat was being suppressed" : "nothing was being suppressed");
            _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedResolve);
            return Task.FromResult(false);
        }

        // Accepted for delivery, like SendAsync -- not "the page is cancelled". The retry on a failed cancel is
        // the pump's job (gh#300); a caller on the flatten path must not wait to find out.
        return Task.FromResult(true);
    }

    /// <summary>
    /// Delivers queued work until the queue is empty, and returns how many items went out.
    /// </summary>
    /// <remarks>
    /// Public because "flush what is pending, now" is a real operation, not a test hook: the pump uses it to
    /// drain on shutdown, and a harness that wants deterministic assertions uses it instead of racing a timer.
    /// It does <b>not</b> wait for anything not yet enqueued.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the drain itself, not an individual delivery.</param>
    /// <returns>How many items were delivered.</returns>
    public async Task<int> DrainPendingAsync(CancellationToken cancellationToken)
    {
        int delivered = 0;
        while (!cancellationToken.IsCancellationRequested && _queue.Reader.TryRead(out Delivery? item))
        {
            await DeliverAsync(item);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Runs the pump until <paramref name="stoppingToken"/> is cancelled, then drains what is left.</summary>
    /// <param name="stoppingToken">The host's stopping token.</param>
    internal async Task PumpAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (Delivery item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Falling through to the drain below is the point: a page queued as the host stops is raised
            // BECAUSE something went wrong, so dropping it loses exactly the alert worth keeping.
        }

        int flushed = await DrainPendingAsync(CancellationToken.None);
        if (flushed > 0)
        {
            _logger.LogInformation("Flushed {Count} pending notification(s) on shutdown.", flushed);
        }
    }

    /// <summary>
    /// Delivers one item, absorbing any fault. A delivery that throws must never take the pump down — the first
    /// failure would otherwise silence every notification after it.
    /// </summary>
    private async Task DeliverAsync(Delivery item)
    {
        try
        {
            // CancellationToken.None deliberately: the caller's token belongs to the FLATTEN. Inheriting it would
            // let a finished pass -- or a stopping host -- cancel the very page explaining why it failed.
            if (item.Notification is not null)
            {
                // A failed SEND is deliberately not retried here: DedupingNotificationChannel below declines to
                // record an incident it could not report, so the next escalation pass re-sends it naturally.
                // Retrying here as well would double-page.
                await _inner.SendAsync(item.Notification, CancellationToken.None);
                return;
            }

            if (await _inner.ResolveAsync(item.DedupKey, CancellationToken.None))
            {
                return;
            }

            // gh#300: the cancel did not land, so an Emergency page is still nagging about a condition that has
            // already cleared. Retry it here -- this is off the flatten hot path, so it costs the safety path
            // nothing. Bounded, because a permanently-rejected receipt would otherwise spin on this single-reader
            // pump and starve every delivery behind it.
            if (item.Attempt + 1 >= MaxResolveAttempts)
            {
                _logger.LogWarning(
                    "Gave up cancelling the page for {Incident} after {Attempts} attempts — it will nag until it expires.",
                    item.DedupKey, MaxResolveAttempts);
                return;
            }

            // Straight into the reserve, with no soft cap: this is a resolve. The key it belongs to has already been
            // released by the decorator below -- it re-arms unconditionally, before forwarding -- so what is at
            // stake here is only the outstanding page's cancel. Worth a reserved slot; not worth crowding out a
            // live page for.
            if (!_queue.Writer.TryWrite(item.NextAttempt()))
            {
                _logger.LogError(
                    "Notification queue is full — REFUSED the cancel retry for {Incident}; its page will nag until "
                    + "it expires or is acknowledged.",
                    item.DedupKey);
                _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedResolve);
            }
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Delivering a notification for {Incident} failed.", item.DedupKey);
        }
    }

    /// <summary>One queued unit of work: a notification to send, or an incident to close.</summary>
    private sealed record Delivery(Notification? Notification, string DedupKey, int Attempt = 0)
    {
        public static Delivery Send(Notification notification) => new(notification, notification.DedupKey);

        public static Delivery Resolve(string dedupKey) => new(null, dedupKey);

        /// <summary>The same resolve, counted as one more attempt (gh#300).</summary>
        public Delivery NextAttempt() => this with { Attempt = Attempt + 1 };
    }
}
