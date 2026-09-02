using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The lifecycle loop both Tradovate socket hosts run (R-17, gh#977, gh#1054): connect the socket at startup, drive
/// it back up with backoff when the client has given up on it, wait while the client is mid-attempt, and finish the
/// work the manual connect path skips. Idle — not a failure — in a deployment where Tradovate is not configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a base class and not a second copy.</b> The market-data and trading hosts shipped a week apart as
/// hand-maintained copies of this loop, and they diverged on a safety-relevant line: a successful connect resets the
/// backoff in one and did not in the other. The consequence was a socket sitting <c>Connected</c> — and therefore
/// healthy-looking to every other reader in the process — while silent for up to a minute per retry, at the moment a
/// rate limit is likeliest. Nothing in CI could have caught it, because both copies were internally consistent and
/// both suites were green. A remark asking every future editor to carry a fix across is a guard that <i>inspects</i>;
/// one implementation is a guard that holds by construction, and gh#41's venue pattern means the copy count only
/// goes up (gh#1054).
/// </para>
/// <para>
/// <b>The two client gaps this loop exists to close</b>, for either socket. The client's internal reconnect
/// <b>gives up after a single failed attempt</b> and parks the socket in
/// <see cref="ClientModels.ConnectionState.Disconnected"/> for the rest of the session — so a blip longer than one
/// attempt would end that feed until the process restarts. And the manual connect that is then the only way out of
/// that state finishes <i>less</i> work than the client's own reconnect does: it replays no subscription and sends
/// no sync. A socket recovered that way is connected, authorized and <b>silent</b>, with nothing raised anywhere.
/// So this loop retries from <c>Disconnected</c> with backoff, and always follows a connect it drove with the
/// derived host's own post-connect obligation.
/// </para>
/// <para>
/// <b>The backoff and its reset.</b> A failed connect, and a pass that still owes its post-connect work, both charge
/// the current backoff as the next delay and then double it toward <see cref="DefaultMaxBackoff"/> — the usual
/// reason either fails is a rate limit, which retrying at full cadence would sustain. The backoff resets in exactly
/// two places: immediately after a successful connect, <b>before</b> the post-connect obligation runs, because
/// whatever was refusing connections has demonstrably just stopped and the outage's accumulated delay must not be
/// charged to the first attempt that follows it; and on any pass that ends owing nothing.
/// </para>
/// <para>
/// <b>What the derived host owns, and only that.</b> The post-connect obligation, which genuinely differs in kind:
/// market data replays a register of quote keys and must survive a <i>partial</i> failure per key, while trading
/// sends one <c>user/syncrequest</c> that either lands or does not. The trading host additionally observes
/// <c>ConnectionStatusChanged</c> to re-arm through <see cref="Observe"/>, which market data does not need — that
/// socket cannot silently reach <c>Connected</c>-but-unsubscribed, because the client's replay failure propagates
/// and parks it in <c>Disconnected</c>, whereas the trading socket can reach <c>Connected</c> unsynced without
/// failing at all. That asymmetry is a difference on purpose; flattening it would introduce the bug this class
/// removes.
/// </para>
/// <para>
/// <b>A degradation nobody is told about is the same defect one layer up (gh#1051).</b> Every failure above — a
/// connect that keeps failing, a post-connect obligation that keeps failing — used to leave exactly one trace: an
/// <c>ILogger</c> line at the backoff cadence, which reaches an engineer reading structured logs and never the
/// operator. The socket meanwhile reports <c>Connected</c> or climbs back to it, so nothing downstream can tell
/// either. After <see cref="DegradedPassesBeforeAdvisory"/> consecutive passes that end still owing, this loop
/// therefore raises an operator-facing advisory through <see cref="INotificationChannel"/> (ADR-0019, P2 — notify),
/// and <b>resolves it the moment a pass owes nothing</b>. The resolve is not optional bookkeeping:
/// <c>DedupingNotificationChannel</c> is a process-lifetime singleton that releases a key only through
/// <c>ResolveAsync</c>, so without it the first outage of the process would deliver and every later, independent one
/// would be silently suppressed as a duplicate — "one notification per process lifetime" instead of one per outage,
/// which is this very failure reproduced one layer down (the blocking review finding on gh#1045).
/// </para>
/// <para>
/// <b>Why the escalation lives here and not in the trading host.</b> gh#1051 was filed against the trading socket,
/// but "connected, degraded, and the only trace is a repeating log line" is not trading-specific — the market-data
/// host has the identical shape, and a stalled quote feed is what stops a hidden stop being promoted (gh#209). Both
/// hosts already share this loop (gh#1054), so the policy is written once and inherited rather than copied, and a
/// third venue's socket host (gh#41) gets it for free. The threshold is deliberately small but not one: a single
/// failed pass is an ordinary blip that the next pass usually clears, while three consecutive ones have spent the
/// first two doublings of the backoff and are no longer plausibly transient.
/// </para>
/// <para>
/// <b>The advisory is a report, never an action.</b> This loop does not tear a socket down, re-authenticate, or
/// disable a venue on its own judgement — it surfaces the state and lets the operator act, the propose-and-confirm
/// posture ratified for detection on this project (gh#722). What it drives is only the recovery it already drove
/// before: connect, and finish the post-connect obligation.
/// </para>
/// <para>
/// <b>The channel is resolved per send, from its own scope.</b> <see cref="INotificationChannel"/> binds to the
/// scoped outbox seam (gh#437), so holding one for the process lifetime would be a captive dependency over a
/// disposed <c>DbContext</c>. Its <i>absence</i> is logged as an error rather than standing the host down: alerting
/// being unregistered must not turn "the operator is not told" into "the venue has no feed at all", which would be
/// the strictly worse failure.
/// </para>
/// <para>
/// <b>Everything is caught, and the loop never ends on a fault.</b> Under the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> an exception escaping <see cref="ExecuteAsync"/> stops the
/// whole application — the auto-flatten watchdog and the kill switch with it. Missing Tradovate credentials are
/// enough to throw while the client is constructed, which is why the resolve is <b>lazy</b>, inside the run rather
/// than through the constructor: the failure direction here is always "this venue's feed degrades", never "the
/// platform will not run" (engineering §9). An <see cref="OperationCanceledException"/> is treated as a stop only
/// <c>when (stoppingToken.IsCancellationRequested)</c>, so a venue timeout arriving as one on an
/// <c>HttpClient</c>-internal token is retried rather than mistaken for a clean shutdown.
/// </para>
/// </remarks>
public abstract class TradovateSocketConnectionHost : BackgroundService
{
    /// <summary>How often the socket's state is sampled, and the first delay before a retried pass.</summary>
    private static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling the backoff doubles up to, so a long outage is retried without hammering it.</summary>
    private static TimeSpan DefaultMaxBackoff { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many consecutive passes may end still owing before the operator is told (gh#1051, ADR-0019 §4).
    /// </summary>
    /// <remarks>
    /// Small, but deliberately not one. A single failed pass is an ordinary blip the next pass usually clears, and
    /// paging on it would spend the noise budget on nothing; three consecutive ones have already spent the first two
    /// doublings of the backoff and are no longer plausibly transient. Counted in <i>passes</i> rather than seconds
    /// because the delay between them grows with the backoff, so a fixed wall-clock threshold would mean something
    /// different at the start of an outage than at its ceiling.
    /// </remarks>
    internal const int DegradedPassesBeforeAdvisory = 3;

    private readonly IServiceProvider _services;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;

    /// <summary>Creates the host.</summary>
    /// <param name="services">
    /// The <b>root</b> provider — the client and its collaborators are resolved from it lazily, once, and held for
    /// the process lifetime, because the thing being owned is a process-wide singleton socket.
    /// </param>
    /// <param name="logger">The logger, categorised to the derived host.</param>
    /// <param name="pollInterval">How often the socket's state is sampled; production cadence when null.</param>
    /// <param name="maxBackoff">The ceiling the backoff doubles up to; production cadence when null.</param>
    protected TradovateSocketConnectionHost(
        IServiceProvider services,
        ILogger logger,
        TimeSpan? pollInterval = null,
        TimeSpan? maxBackoff = null)
    {
        _services = services;
        Logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _maxBackoff = maxBackoff ?? DefaultMaxBackoff;
    }

    /// <summary>The logger, categorised to the derived host.</summary>
    protected ILogger Logger { get; }

    /// <summary>Names this socket in every log message — <c>market-data</c> or <c>trading</c>.</summary>
    protected abstract string SocketName { get; }

    /// <summary>
    /// What is lost while this socket is down, as a clause — e.g. <c>Tradovate market data is not flowing</c>. It is
    /// the operator-facing half of every failure log, so it names the <i>feed</i>, never the mechanism.
    /// </summary>
    protected abstract string SilenceConsequence { get; }

    /// <summary>Reads this socket's state from the client.</summary>
    protected abstract ClientModels.ConnectionState ReadState(ITradovateWebSocketClient client);

    /// <summary>Drives the manual connect for this socket. Throwing means the attempt failed.</summary>
    protected abstract Task ConnectSocketAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves whatever else this host needs, from the same root provider and inside the same guarded resolve.
    /// Returning false stands the host down: log why, and never throw.
    /// </summary>
    protected abstract bool TryResolveCollaborators(IServiceProvider services);

    /// <summary>
    /// Attaches whatever client events this host watches, before the first pass so a transition that lands between
    /// the resolve and the loop is still seen. The returned handle is disposed when the run ends, so attach and
    /// detach cannot drift apart. Null when this host watches nothing — which is the market-data case, deliberately.
    /// </summary>
    protected virtual IDisposable? Observe(ITradovateWebSocketClient client) => null;

    /// <summary>
    /// Finishes the work the <b>manual</b> connect this host just drove does not do. Returns true when the socket
    /// owes nothing more; false leaves the pass charging the backoff and retrying on the next one.
    /// </summary>
    protected abstract Task<bool> SettleAfterHostDrivenConnectAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes whatever a socket found already <see cref="ClientModels.ConnectionState.Connected"/> still owes —
    /// which may be nothing. Returns true when the socket owes nothing more; false backs off and retries.
    /// </summary>
    protected abstract Task<bool> SettleConnectedSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken);

    /// <summary>Detaches an <see cref="Observe"/> subscription, so the attach and its undo live in one place.</summary>
    /// <param name="detach">Runs on dispose.</param>
    protected sealed class Detach(Action detach) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => detach();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sealed: the loop below is the whole point of this class, and a derived host that re-implemented it would be
    /// the copy this type exists to prevent.
    /// </remarks>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The outermost net. A cancellation or a fault that reached here would leave ExecuteTask cancelled or
        // faulted, and under BackgroundServiceExceptionBehavior.StopHost a faulted one stops the application. The
        // per-pass handling below already covers the expected cases; this is what makes "nothing escapes" true
        // rather than merely intended.
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // a clean stop
        }
        catch (ObjectDisposedException)
        {
            // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
        }
        catch (Exception error)
        {
            Logger.LogError(
                error,
                "The Tradovate {Socket} connection host stopped unexpectedly; {Consequence}, and will not recover "
                + "until the process restarts.",
                SocketName,
                SilenceConsequence);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Resolve() is not { } client)
        {
            return;
        }

        using IDisposable? observation = Observe(client);
        await PollAsync(client, stoppingToken);
    }

    private async Task PollAsync(ITradovateWebSocketClient client, CancellationToken stoppingToken)
    {
        TimeSpan backoff = _pollInterval;

        // gh#1051. Local to the loop rather than fields: this loop is single-threaded, so nothing needs
        // synchronising, and a host that is restarted cannot inherit a stale incident from an earlier run.
        int degradedPasses = 0;
        bool advised = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = _pollInterval;

            // Did this pass leave the socket owing something? `null` means it proved nothing either way -- the
            // client is mid-attempt, or the state is unrecognised -- so it must neither escalate an outage nor
            // declare one over. Only "the socket owes nothing" clears; only "it still owes" counts against the
            // threshold.
            bool? owing = null;

            try
            {
                switch (ReadState(client))
                {
                    case ClientModels.ConnectionState.Connected:
                        if (await SettleConnectedSocketAsync(client, stoppingToken))
                        {
                            // A pass that owes nothing returns the cadence to the poll interval.
                            backoff = _pollInterval;
                            owing = false;
                        }
                        else
                        {
                            delay = backoff;
                            backoff = NextBackoff(backoff);
                            owing = true;
                        }

                        break;

                    case ClientModels.ConnectionState.Connecting:
                    case ClientModels.ConnectionState.Reconnecting:
                        // The client is mid-attempt, and its own reconnect finishes MORE than the manual path does.
                        // Driving a manual connect now would tear that attempt down and land on the lesser path.
                        // No wire traffic is sent on this pass, so there is no rate limit to back off from either.
                        //
                        // `owing` deliberately stays null. An attempt in progress is not yet a failure, and one
                        // that never finishes is a distinct defect with its own card (gh#1052) rather than
                        // something this counter should quietly absorb.
                        break;

                    case ClientModels.ConnectionState.Disconnected:
                        if (await TryConnectAsync(client, stoppingToken))
                        {
                            // Reset BEFORE the post-connect obligation, not after it. Whatever was refusing
                            // connections has demonstrably just stopped, so the outage's accumulated backoff must
                            // not be charged to the first attempt that follows: a rate limit is likeliest right
                            // after a reconnect, and work that failed while `backoff` still held a 60-second
                            // ceiling would leave a socket that is connected -- and therefore looks healthy to
                            // everything else -- silent for a minute per retry. This is the line the two
                            // hand-maintained copies disagreed about (gh#1054); it now exists once.
                            backoff = _pollInterval;

                            if (await SettleAfterHostDrivenConnectAsync(client, stoppingToken))
                            {
                                owing = false;
                            }
                            else
                            {
                                delay = backoff;
                                backoff = NextBackoff(backoff);
                                owing = true;
                            }
                        }
                        else
                        {
                            delay = backoff;
                            backoff = NextBackoff(backoff);
                            owing = true;
                        }

                        break;

                    default:
                        // An unrecognised state is not evidence the socket is usable, and acting on it could tear
                        // down a working transport — so wait, the same fail-safe direction the liveness seam takes.
                        Logger.LogWarning(
                            "The Tradovate {Socket} socket reported an unrecognised state; waiting rather than "
                            + "reconnecting.",
                            SocketName);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // A transient fault must not stop the application, and must not end the loop either: log, wait a
                // pass, try again. A venue timeout arrives here as an OperationCanceledException carrying
                // HttpClient's own internal token, which is why the clause above tests the STOPPING token.
                //
                // It counts as owing (gh#1051): a pass that threw did not leave a socket delivering anything, and a
                // fault that repeats -- a state read that keeps throwing, say -- is exactly the shape that used to
                // produce a log line at the backoff cadence and nothing else.
                owing = true;
                Logger.LogWarning(error, "The Tradovate {Socket} connection pass failed; retrying.", SocketName);
            }

            // gh#1051: outside the pass's own try, because these must not be mistaken for a connection fault and
            // must not be skipped by one. Neither call throws.
            switch (owing)
            {
                case true:
                    degradedPasses++;
                    if (!advised && degradedPasses >= DegradedPassesBeforeAdvisory)
                    {
                        advised = await AdviseDegradedAsync(degradedPasses, stoppingToken);
                    }

                    break;

                case false:
                    degradedPasses = 0;
                    if (advised)
                    {
                        // Held until the resolve is CONFIRMED. An unconfirmed one would leave the dedup key held
                        // for the life of the process, silently suppressing the next genuine outage.
                        advised = !await ResolveDegradedAsync(stoppingToken);
                    }

                    break;

                default:
                    break;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // a clean stop
            }
        }
    }

    // Tells the OPERATOR that this socket has been degraded for several consecutive passes -- the loud failure that
    // was missing while the only trace was a log line at the backoff cadence (gh#1051, ADR-0019 P2). Reports; never
    // acts. Returns whether the advisory was accepted for delivery, so one that was not is attempted again on the
    // next degraded pass rather than being recorded as told.
    private async Task<bool> AdviseDegradedAsync(int degradedPasses, CancellationToken cancellationToken)
    {
        Notification advisory = new(
            NotificationSeverity.Notify,
            $"Tradovate {SocketName} socket degraded",
            $"{SilenceConsequence}, and the socket has not recovered across {degradedPasses} consecutive attempts. "
            + "It may be reporting itself connected throughout, so nothing downstream can tell — check the API logs "
            + $"for the Tradovate {SocketName} connection host.",
            DegradedDedupKey);

        return await TryNotifyAsync(
            channel => channel.SendAsync(advisory, cancellationToken), "raise the degraded advisory for");
    }

    // Closes the incident the moment a pass owes nothing. Not optional bookkeeping: DedupingNotificationChannel is a
    // process-lifetime singleton that releases a key only through ResolveAsync, so skipping this would deliver the
    // FIRST outage of the process and silently suppress every later one as a duplicate (gh#1045).
    private Task<bool> ResolveDegradedAsync(CancellationToken cancellationToken) =>
        TryNotifyAsync(
            channel => channel.ResolveAsync(DegradedDedupKey, cancellationToken), "resolve the degraded advisory for");

    // One scope per call. INotificationChannel binds to the scoped outbox seam (gh#437), so a channel held for the
    // process lifetime would be a captive dependency over a disposed DbContext. Never throws: the poll loop's job is
    // to keep the socket up, and an alerting fault must not cost a pass of that.
    private async Task<bool> TryNotifyAsync(Func<INotificationChannel, Task<bool>> send, string what)
    {
        try
        {
            using IServiceScope scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService<INotificationChannel>() is not { } channel)
            {
                // Loud, not silent. Standing the host down instead would turn "the operator is not told" into "this
                // venue has no feed at all", which is the strictly worse failure -- but a missing alerting channel
                // must never be discovered only by an outage nobody heard about.
                Logger.LogError(
                    "No {Channel} is registered, so nothing can {What} the Tradovate {Socket} socket and the "
                    + "operator will not be told that {Consequence}.",
                    nameof(INotificationChannel),
                    what,
                    SocketName,
                    SilenceConsequence);
                return false;
            }

            return await send(channel);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown, not a fault: the stopping token fired mid-send, or the root provider is already being torn
            // down. The loop's own checks end the run on the next statement, and logging an alerting ERROR on every
            // clean stop would train the reader to ignore the one that matters.
            Logger.LogDebug(
                error, "Did not {What} the Tradovate {Socket} socket; the host is stopping.", what, SocketName);
            return false;
        }
        catch (Exception error)
        {
            // INotificationChannel's contract is never-throws, so reaching here means a buggy channel -- which must
            // cost the advisory, never the pass that is trying to bring the socket back.
            Logger.LogError(
                error, "Failed to {What} the Tradovate {Socket} socket.", what, SocketName);
            return false;
        }
    }

    // Scoped to the SOCKET, so a degraded market-data feed never suppresses a degraded trading feed. Static for the
    // life of the process and released by ResolveAsync, which is what makes the invariant "one notification per
    // outage" rather than "one per process lifetime".
    private string DegradedDedupKey => $"tradovate.socket.degraded:{SocketName}";

    // Returns null whenever the host must stand down: Tradovate unconfigured (the common, benign case), a client
    // that throws while it is built (bad or absent credentials), or a required collaborator missing while the client
    // is present (a wiring defect -- a socket this host connected but could never finish is precisely the silent
    // socket it exists to prevent, so it must not connect one).
    private ITradovateWebSocketClient? Resolve()
    {
        try
        {
            if (_services.GetService<ITradovateWebSocketClient>() is not { } client)
            {
                Logger.LogInformation(
                    "Tradovate is not configured in this deployment; the {Socket} connection host is idle.",
                    SocketName);
                return null;
            }

            return TryResolveCollaborators(_services) ? client : null;
        }
        catch (Exception error)
        {
            Logger.LogError(
                error,
                "Building the Tradovate websocket client failed; the {Socket} connection host is idle and "
                + "{Consequence}.",
                SocketName,
                SilenceConsequence);
            return null;
        }
    }

    private async Task<bool> TryConnectAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectSocketAsync(client, cancellationToken);
            Logger.LogInformation("Connected the Tradovate {Socket} socket.", SocketName);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Logger.LogWarning(
                error,
                "Connecting the Tradovate {Socket} socket failed; {Consequence}.",
                SocketName,
                SilenceConsequence);
            return false;
        }
    }

    private TimeSpan NextBackoff(TimeSpan backoff) =>
        backoff < _maxBackoff ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _maxBackoff.Ticks)) : _maxBackoff;
}
