using System.Collections.Concurrent;
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
/// exhausted, the dedup key is released out of band through <see cref="IIncidentKeyRegistry"/> — see
/// <see cref="ResolveAsync"/>.
/// </para>
/// <para>
/// <b>Releasing the key is not enough on its own, and the first cut of gh#1077 got this wrong</b> (round-2
/// review). <see cref="DedupingNotificationChannel"/> <i>arms</i> a key on a successful send, so a page still
/// sitting in this queue when a resolve is refused re-arms the key the refusal just released — and the resolve
/// that would have cleared it is gone. The refusal therefore also records the ordinal it was refused at, and the
/// pump releases the key again after delivering any page enqueued at or before that point: FIFO makes "this page
/// is one the lost resolve would have closed" decidable. What is <b>not</b> covered is a page that <i>fails</i>
/// delivery in that window and is re-offered by the outbox later under a fresh ordinal; that is a genuinely new
/// send, so the marker is inert against it and the key stays armed until the producer resolves again. Stated
/// precisely rather than as an absolute, because the Tradovate producer-side re-arm (gh#1051) is the belt that
/// covers exactly that residue and must not be removed on the strength of this class.
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
    /// <para>
    /// Sized against the number of <b>distinct open incidents</b> — a handful of account/instrument pairs plus a
    /// trigger or two, the same bound that lets <c>DedupingNotificationChannel</c> hold its incident set with no
    /// eviction policy. Sixty-four is generous against that and small beside the page budget it protects.
    /// </para>
    /// <para>
    /// <b>That bound only holds because repeats are collapsed</b> (round-2 review). Resolves are <i>not</i> rare:
    /// <c>AutoFlattenService</c> resolves <b>every configured instrument on every 15 s pass</b>, whether or not
    /// anything was ever paged for it, and the watchdog does the same every 20 s. Sized against that rate a
    /// 64-slot reserve is four minutes of a wedged transport, not a residual case — so
    /// <see cref="ResolveAsync"/> does not enqueue a resolve while one for the same key is already queued ahead
    /// of it with no page in between. The queue then holds at most one resolve per open key, which is the number
    /// this constant is actually sized against.
    /// </para>
    /// </remarks>
    public const int ResolveHeadroom = 64;

    private readonly INotificationChannel _inner;
    private readonly IIncidentKeyRegistry _incidents;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<QueuedNotificationChannel> _logger;
    private readonly Channel<Delivery> _queue;

    // SERIALISES ordinal assignment, the channel write, and the bookkeeping below it (gh#1077 round-3 review).
    // Without it the three are independently ordered: a resolve could take an ordinal, be preempted, and have its
    // marker land AFTER a page for the same key had written and cleared -- leaving a resolve queued AHEAD of a
    // page with the collapse marker still set, so the next resolve was dropped and nothing ever closed that page.
    // It also makes the enqueue ordinal agree with the queue's actual order, which the marker logic depends on
    // and which Interlocked-then-write does not guarantee. Held only across a TryWrite and a few dictionary
    // operations -- no I/O, no awaits, nothing that can block -- so it is safe on the R-13 caller.
    private readonly Lock _enqueue = new();

    // Ordinal of the newest PAGE enqueued for each key, and of the newest RESOLVE. A resolve is collapsed into an
    // already-queued one only when the resolve is newer than every page for that key, which is the same question
    // as "is it queued behind all of them" now that ordinals follow queue order.
    private readonly ConcurrentDictionary<string, long> _queuedPages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _queuedResolves = new(StringComparer.Ordinal);

    // Keys whose resolve was REFUSED, against the ordinal of the LAST page that refusal covers -- the newest page
    // queued for the key at that moment. The pump releases the key once, after delivering exactly that page, so
    // the backlog beneath it still dedups to a single push while the key still ends released.
    //
    // Lifetime: an entry is consumed by that page's delivery. It outlives the process only in the case named in
    // the type remarks -- a page whose own delivery straddled the refusal -- and a stale entry is inert, because
    // it can match no other ordinal. Bounded by distinct incident keys, the same handful that lets
    // DedupingNotificationChannel hold its incident set with no eviction policy.
    private readonly ConcurrentDictionary<string, long> _lostResolves = new(StringComparer.Ordinal);

    // Monotonic enqueue ordinal, assigned and consumed under _enqueue so it follows the queue's actual order.
    private long _writes;

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

        bool accepted;
        lock (_enqueue)
        {
            // The soft cap is read inside the lock with the write, so pages stop at PageCapacity exactly. It caps
            // TOTAL depth, so queued resolves count against it and the effective page budget during a wedge is a
            // little under PageCapacity -- the conservative direction. The reserve above it belongs to resolves
            // and is not overshoot room for pages.
            long ordinal = _writes + 1;
            accepted = _queue.Reader.Count < PageCapacity
                && _queue.Writer.TryWrite(Delivery.Send(notification, ordinal));

            if (accepted)
            {
                _writes = ordinal;
                _queuedPages[notification.DedupKey] = ordinal;
            }
        }

        if (!accepted)
        {
            // ERROR, deliberately, and never Debug: appsettings.json sets Logging:LogLevel:Default to Information,
            // so anything below that is never written in production -- a "logged" drop nobody can read is the
            // defect being fixed, not the fix. Metered as well (gh#1077), because a log line is visible to an
            // engineer who goes looking and the operator is exactly who is not being told. Outside the lock: a
            // logging sink is the one thing here that could block.
            _logger.LogError(
                "Notification queue is full — REFUSED a {Severity} for {Incident}; the transport is not draining. "
                + "The page is not lost: it stays owed in the outbox and is re-offered on the next relay pass.",
                notification.Severity, notification.DedupKey);
            _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedPage);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Whether a resolve can be collapsed into one already queued for the same key (gh#1077 round-3 review).
    /// </summary>
    /// <remarks>
    /// The whole decision, as a pure function of two ordinals, because the bug it replaces was one of <i>store
    /// order</i> rather than of policy: the previous rule asked whether a marker was <b>present</b>, so a
    /// preempted thread could set it after a page for the same key had already cleared it. Comparing values that
    /// cannot go backwards makes the answer independent of the order the two bookkeeping writes happen in.
    /// Exposed so the orderings a race produces can be asserted directly, including the one no single-threaded
    /// fixture can reach.
    /// </remarks>
    /// <param name="queuedResolveOrdinal">Ordinal of the newest resolve queued for the key.</param>
    /// <param name="newestPageOrdinal">Ordinal of the newest page queued for the key, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the queued resolve sits behind every queued page for that key.</returns>
    public static bool CollapsesIntoQueuedResolve(long queuedResolveOrdinal, long? newestPageOrdinal) =>
        newestPageOrdinal is not { } page || queuedResolveOrdinal > page;

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
        // Guarded because the fallback below reaches a ConcurrentDictionary, which throws on a null key -- and
        // this method is called from the flatten under a never-throw contract. Before gh#1077 a bad key surfaced
        // inside the pump's catch; it must not start escaping onto the R-13 thread instead.
        ArgumentException.ThrowIfNullOrEmpty(dedupKey);

        bool accepted;
        bool released = false;
        lock (_enqueue)
        {
            // COLLAPSE a repeat (gh#1077 round-2 review). AutoFlattenService resolves EVERY configured instrument
            // on EVERY 15 s pass, paged or not, so a wedged transport meets a stream of resolves rather than the
            // handful the reserve is sized for. A resolve already queued BEHIND every page for this key will
            // release the key and cancel the page exactly as this one would -- the operation is keyed and
            // idempotent -- so `true` here still means what it always meant: accepted for delivery, by the item
            // already carrying it.
            if (_queuedResolves.TryGetValue(dedupKey, out long queuedResolveAt)
                && CollapsesIntoQueuedResolve(
                    queuedResolveAt, _queuedPages.TryGetValue(dedupKey, out long pageAt) ? pageAt : null))
            {
                return Task.FromResult(true);
            }

            long ordinal = _writes + 1;
            accepted = _queue.Writer.TryWrite(Delivery.Resolve(dedupKey, ordinal));

            if (accepted)
            {
                _writes = ordinal;
                _queuedResolves[dedupKey] = ordinal;
            }
            else
            {
                // THE BRACE (gh#1077). A resolve carries two halves that fail differently. Cancelling the
                // outstanding page needs the transport, so it is lost here and `false` asks the caller to try
                // again -- INotificationChannel's documented meaning for `false`. Releasing the dedup key needs
                // nothing but a dictionary removal, and NOTHING anywhere remembers that it is owed: there is no
                // outbox row for a resolve, and a producer that resolves once per outage never comes back. Left
                // held, the key suppresses every later incident on it for the life of the process -- ADR-0019's
                // "one notification per process lifetime instead of one per outage" (gh#1045, gh#1051).
                //
                // TWO things, because releasing alone does not hold (round-2 review). ReleaseIncident clears a key
                // armed NOW; the marker covers the pages already queued for this key, which would otherwise re-arm
                // it on delivery. It records the NEWEST such page, and the pump releases once after delivering
                // exactly that one -- so the backlog beneath it still dedups to a single push instead of one
                // Emergency push per queued page (round-3 review), and the key still ends released. With no page
                // queued there is nothing that can re-arm it, so no marker is recorded at all.
                if (_queuedPages.TryGetValue(dedupKey, out long coverUpTo))
                {
                    _lostResolves[dedupKey] = coverUpTo;
                }

                released = _incidents.ReleaseIncident(dedupKey);
            }
        }

        if (!accepted)
        {
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

                // gh#1077 round-2 review: a successful send ARMS the key below, so a page queued behind a refused
                // resolve would silently undo that refusal's out-of-band release. Round-3: release ONCE, after the
                // LAST page that refusal covered -- every page beneath it is suppressed by the key it armed, so
                // one incident stays one Emergency push, and the key still ends released. A page enqueued AFTER
                // the refusal never matches, so a new incident keeps its suppression.
                bool releaseNow;
                lock (_enqueue)
                {
                    _queuedPages.TryRemove(new KeyValuePair<string, long>(item.DedupKey, item.Ordinal));
                    releaseNow = _lostResolves.TryGetValue(item.DedupKey, out long coverUpTo)
                        && item.Ordinal == coverUpTo;

                    if (releaseNow)
                    {
                        _lostResolves.TryRemove(item.DedupKey, out _);
                    }
                }

                if (releaseNow)
                {
                    _incidents.ReleaseIncident(item.DedupKey);
                }

                return;
            }

            // Off the queue, so the next resolve for this key must be enqueued rather than collapsed into it.
            // Ordinal-matched, so a NEWER resolve recorded meanwhile is left alone.
            lock (_enqueue)
            {
                _queuedResolves.TryRemove(new KeyValuePair<string, long>(item.DedupKey, item.Ordinal));
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
            // Error rather than Critical, unlike a refused resolve from a caller, and the difference is real: the
            // dedup layer re-armed this key unconditionally on the call above, so nothing here can leave a key
            // held. What is lost is only the cancel, so the page nags until it expires -- bad, not silent.
            bool requeued;
            lock (_enqueue)
            {
                requeued = _queue.Writer.TryWrite(item.NextAttempt());
                if (requeued)
                {
                    // The retry keeps its ORIGINAL ordinal, so a page enqueued since is newer and correctly stops
                    // the next resolve collapsing into it. Max, so a newer resolve recorded meanwhile wins.
                    _queuedResolves.AddOrUpdate(
                        item.DedupKey, item.Ordinal, (_, existing) => Math.Max(existing, item.Ordinal));
                }
            }

            if (!requeued)
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
    /// <remarks>
    /// <paramref name="Ordinal"/> is the monotonic enqueue position (gh#1077). It exists so the pump can decide
    /// whether a page it is delivering was queued <i>before</i> a resolve that was later refused — the one thing
    /// that tells an undone out-of-band release apart from a genuinely new incident.
    /// </remarks>
    private sealed record Delivery(Notification? Notification, string DedupKey, long Ordinal, int Attempt = 0)
    {
        public static Delivery Send(Notification notification, long ordinal) =>
            new(notification, notification.DedupKey, ordinal);

        public static Delivery Resolve(string dedupKey, long ordinal) => new(null, dedupKey, ordinal);

        /// <summary>The same resolve, counted as one more attempt (gh#300) — its ordinal is unchanged.</summary>
        public Delivery NextAttempt() => this with { Attempt = Attempt + 1 };
    }
}
