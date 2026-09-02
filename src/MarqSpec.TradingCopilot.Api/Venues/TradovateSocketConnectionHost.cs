using MarqSpec.Client.Tradovate.WebSocket;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = _pollInterval;

            try
            {
                switch (ReadState(client))
                {
                    case ClientModels.ConnectionState.Connected:
                        if (await SettleConnectedSocketAsync(client, stoppingToken))
                        {
                            // A pass that owes nothing returns the cadence to the poll interval.
                            backoff = _pollInterval;
                        }
                        else
                        {
                            delay = backoff;
                            backoff = NextBackoff(backoff);
                        }

                        break;

                    case ClientModels.ConnectionState.Connecting:
                    case ClientModels.ConnectionState.Reconnecting:
                        // The client is mid-attempt, and its own reconnect finishes MORE than the manual path does.
                        // Driving a manual connect now would tear that attempt down and land on the lesser path.
                        // No wire traffic is sent on this pass, so there is no rate limit to back off from either.
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

                            if (!await SettleAfterHostDrivenConnectAsync(client, stoppingToken))
                            {
                                delay = backoff;
                                backoff = NextBackoff(backoff);
                            }
                        }
                        else
                        {
                            delay = backoff;
                            backoff = NextBackoff(backoff);
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
                Logger.LogWarning(error, "The Tradovate {Socket} connection pass failed; retrying.", SocketName);
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
