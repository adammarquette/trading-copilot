using MarqSpec.Client.Tradovate.Authentication;
using MarqSpec.Client.Tradovate.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// Owns the lifecycle of the process-wide Tradovate <b>trading socket</b> (R-17, gh#977): it connects the socket at
/// startup, drives it back up when the client has given up on it, and sends the <c>user/syncrequest</c> that is the
/// only thing which makes order, position and fill events arrive at all. Idle — not a failure — in a deployment
/// where Tradovate is not configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sibling of <c>TradovateMarketDataConnectionHost</c>, for the other socket.</b> Same two client gaps, one
/// different obligation after a connect. Where the market-data host replays a register of quote keys, this one sends
/// a single snapshot request — so the two loops are deliberately kept separate rather than folded behind a template
/// method: what "finish the connect" means differs in kind (a per-key replay that must survive partial failure,
/// versus one request that either lands or does not), and the market-data host is a just-reviewed safety path with no
/// behavioural reason to be re-derived.
/// </para>
/// <para>
/// <b>The gap a host-driven connect leaves.</b> <c>ConnectTradingAsync</c> opens the transport and authorizes it —
/// and stops. The client sends <c>user/syncrequest</c> only from its <i>own</i> internal reconnect path, and
/// Tradovate pushes <c>props</c> entity frames only to a socket that has synced. A socket brought back by the manual
/// connect is therefore connected, authorized and <b>permanently silent</b>: no order, no fill, no position update,
/// and nothing raised anywhere. Every reconciliation and journalling path downstream would simply see a quiet
/// account. So a connect this host drove is always followed by its own sync, on the same pass.
/// </para>
/// <para>
/// <b>What is verified here and what is not.</b> Everything above about the <i>client</i> is read from its source:
/// the manual connect's <c>replay: false</c>, the reconnect's single attempt, its <c>if (userId is { } id)</c> guard
/// around the sync, and that the sync's only in-client caller is that reconnect. That Tradovate itself pushes
/// <c>props</c> only to a <i>synced</i> socket is the one premise this side cannot pin — the client dispatches
/// <c>props</c> unconditionally and neither it nor its docs state the server's rule. It is the protocol as
/// documented and as the client's own reconnect assumes, and syncing an already-synced socket costs a duplicate
/// snapshot rather than a fault, so the host is built on it — but like the duplicate-sync question it is a staging
/// observation to make once credentials exist, not a fact this repo has established.
/// </para>
/// <para>
/// <b>The gap the client's recovery leaves.</b> That internal reconnect <b>gives up after a single failed attempt</b>
/// and parks the socket in <see cref="ClientModels.ConnectionState.Disconnected"/> for the rest of the session, so a
/// blip longer than one attempt would end order events until the process restarts. This host retries from
/// <c>Disconnected</c> with backoff. While the client reports <see cref="ClientModels.ConnectionState.Connecting"/>
/// or <see cref="ClientModels.ConnectionState.Reconnecting"/> it waits instead: the manual connect would tear that
/// attempt down and land on the non-syncing path.
/// </para>
/// <para>
/// <b>Why a grace pass before syncing a socket somebody else connected.</b> The client's reconnect issues the sync in
/// the statement after it reports <c>Connected</c>. Syncing the moment this host observed that transition would put a
/// duplicate <c>user/syncrequest</c> on the <i>ordinary</i> path — and a sync is a full snapshot of every order, fill
/// and position, so a duplicate is re-delivered work for every consumer, not a no-op. What Tradovate itself does with
/// a second sync is unverified from this side; it is a staging observation to make once credentials exist. So a
/// connect this host did not drive is given one pass, and only then synced — which still repairs the case the client
/// leaves silent <i>without</i> failing: its reconnect skips the sync entirely when the authenticated user id is
/// unavailable, and reports <c>Connected</c> regardless.
/// </para>
/// <para>
/// <b>One pass of grace, and the direction it errs in is deliberate.</b> "The statement after <c>Connected</c>" is
/// <c>await GetUserIdAsync()</c>, which can run a full REST token renewal before the socket round trip that follows
/// — so a single poll interval is not certainly longer than the client's own sync takes, and a slow or throttled
/// venue can still draw a duplicate out of this host. Waiting longer would make that rarer at the cost of a longer
/// silence, and the two costs are not symmetric: a duplicate snapshot is re-delivered work for consumers that have
/// to be idempotent anyway, while a silent trading socket is a position the platform cannot see. So the grace stays
/// at the shortest value that covers the ordinary case, and the residual duplicate is accepted rather than traded
/// for latency on the repair path.
/// </para>
/// <para>
/// <b>The need is cleared by a snapshot landing, never by an attempt.</b> A failed sync leaves a socket that looks
/// healthy, so nothing would ever revisit it; the need therefore survives into the next pass, since the usual reason
/// a sync fails — a rate limit — is one that retrying at full cadence would sustain. A reconnect that overtakes an
/// in-flight sync <i>usually</i> faults it rather than letting it complete: the client fails every request still
/// pending as it rebuilds the transport. That is not a guarantee, though, and the loop does not lean on it as one.
/// A response the receive loop has already dispatched resolves regardless of what happens next, so a completion can
/// in principle arrive after a fresh connect has re-armed the need and clear it — one thread-pool continuation
/// racing a whole transport rebuild and authorize round trip. The pass-level clear is therefore conditional
/// (<c>CompareExchange</c> from <c>Due</c> only), and it is the event handler, which cannot know which connection a
/// completion belongs to, that carries the residual risk. Closing it needs connection identity the client's event
/// does not carry; it is named in gh#1051 rather than papered over here.
/// </para>
/// <para>
/// <b>Everything is caught.</b> Under the default <c>BackgroundServiceExceptionBehavior.StopHost</c> an exception
/// escaping <see cref="ExecuteAsync"/> stops the whole application — the auto-flatten watchdog and the kill switch
/// with it. Missing Tradovate credentials are enough to throw while the client is constructed, so the failure
/// direction here is always "this venue's account feed degrades", never "the platform will not run" (engineering §9).
/// </para>
/// <para>
/// This host does not <i>consume</i> the events it makes flow, and it places nothing: <c>user/syncrequest</c> is a
/// read. Translating Tradovate's order / position / fill entities onto the neutral account-event seam is the next
/// slice of gh#977, as is the venue's own runtime wiring.
/// </para>
/// </remarks>
public sealed class TradovateTradingConnectionHost : BackgroundService
{
    /// <summary>How often the socket's state is sampled, and the first delay before a retried connect or sync.</summary>
    private static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling the backoff doubles up to, so a long outage is retried without hammering it.</summary>
    private static TimeSpan DefaultMaxBackoff { get; } = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<TradovateTradingConnectionHost> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;

    // Written from the client's event handlers (arbitrary threads) as well as the loop, so every read and write goes
    // through Interlocked/Volatile. Starts at Pending rather than None: a socket already Connected when this host
    // starts was synced by nothing this process can see, and a possibly-duplicate snapshot is the right way to be
    // wrong about that -- a silent trading socket is not.
    private int _syncNeed = (int)SyncNeed.Pending;

    /// <summary>Creates the host with the production cadence.</summary>
    /// <param name="services">The root provider — the client and its authentication service resolve from it lazily.</param>
    /// <param name="logger">The logger.</param>
    public TradovateTradingConnectionHost(
        IServiceProvider services, ILogger<TradovateTradingConnectionHost> logger)
        : this(services, logger, DefaultPollInterval, DefaultMaxBackoff)
    {
    }

    /// <summary>Creates the host with an explicit cadence, so a test does not wait out the production delays.</summary>
    /// <param name="services">The root provider.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pollInterval">How often the socket's state is sampled.</param>
    /// <param name="maxBackoff">The ceiling the backoff doubles up to.</param>
    internal TradovateTradingConnectionHost(
        IServiceProvider services,
        ILogger<TradovateTradingConnectionHost> logger,
        TimeSpan pollInterval,
        TimeSpan maxBackoff)
    {
        _services = services;
        _logger = logger;
        _pollInterval = pollInterval;
        _maxBackoff = maxBackoff;
    }

    // What the trading socket still owes before it is actually delivering entity events. Ordered by how certain the
    // host is that it must act, because the two "connected" cases are not the same obligation.
    private enum SyncNeed
    {
        /// <summary>A snapshot has landed since the socket last connected — the feed is live.</summary>
        None = 0,

        /// <summary>
        /// The socket reported <c>Connected</c> and this host did not drive it, so the client's own reconnect may be
        /// about to sync it. One pass of patience, then <see cref="Due"/>.
        /// </summary>
        Pending = 1,

        /// <summary>Nothing else will sync this connection. Send it.</summary>
        Due = 2,
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            _logger.LogError(
                error,
                "The Tradovate trading connection host stopped unexpectedly; Tradovate order, fill and position "
                + "events will not recover until the process restarts.");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Resolve LAZILY, inside the run: constructing the client touches credentials the process (and every test
        // host) may lack at startup, and an eager injection would crash the host instead of degrading this feed.
        (ITradovateWebSocketClient? client, IAuthenticationService? authentication) = Resolve();
        if (client is null || authentication is null)
        {
            return;
        }

        // Attached BEFORE the first pass, so a connect that lands between the resolve and the loop is still seen.
        client.ConnectionStatusChanged += OnConnectionStatusChanged;
        client.SyncCompleted += OnSyncCompleted;
        try
        {
            await PollAsync(client, authentication, stoppingToken);
        }
        finally
        {
            client.ConnectionStatusChanged -= OnConnectionStatusChanged;
            client.SyncCompleted -= OnSyncCompleted;
        }
    }

    private async Task PollAsync(
        ITradovateWebSocketClient client, IAuthenticationService authentication, CancellationToken stoppingToken)
    {
        TimeSpan backoff = _pollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = _pollInterval;

            try
            {
                switch (client.TradingState)
                {
                    case ClientModels.ConnectionState.Connected:
                        switch ((SyncNeed)Volatile.Read(ref _syncNeed))
                        {
                            case SyncNeed.Pending:
                                // Somebody else connected it. The client's own reconnect syncs in the statement
                                // after it reports Connected, so give that one pass rather than duplicating a full
                                // snapshot; if it never lands, the next pass sends ours.
                                Interlocked.CompareExchange(
                                    ref _syncNeed, (int)SyncNeed.Due, (int)SyncNeed.Pending);
                                backoff = _pollInterval;
                                break;

                            case SyncNeed.Due:
                                if (await SyncAsync(client, authentication, stoppingToken))
                                {
                                    // Only from Due: a drop that re-armed the need mid-sync must not be cleared here.
                                    Interlocked.CompareExchange(
                                        ref _syncNeed, (int)SyncNeed.None, (int)SyncNeed.Due);
                                    backoff = _pollInterval;
                                }
                                else
                                {
                                    delay = backoff;
                                    backoff = NextBackoff(backoff);
                                }

                                break;

                            default:
                                backoff = _pollInterval;
                                break;
                        }

                        break;

                    case ClientModels.ConnectionState.Connecting:
                    case ClientModels.ConnectionState.Reconnecting:
                        // The client is mid-attempt, and its own reconnect sends the sync itself. Driving a manual
                        // connect now would tear that attempt down and land on the path that never syncs.
                        break;

                    case ClientModels.ConnectionState.Disconnected:
                        if (await ConnectAsync(client, stoppingToken))
                        {
                            // Reset BEFORE the sync, not after it. Whatever was refusing connections has
                            // demonstrably just stopped, so the outage's accumulated backoff must not be charged to
                            // the first sync attempt: a rate limit is likeliest right after a reconnect, and a sync
                            // that failed while `backoff` still held a 60-second ceiling would leave a socket that
                            // is connected -- and therefore looks healthy to everything else -- silent for a minute
                            // per retry. The market-data sibling resets in the same place, for the same reason.
                            backoff = _pollInterval;

                            // A host-driven connect authorizes the socket and sends nothing else, so the sync is
                            // owed with no grace: nothing in the process will ever send it otherwise, and until it
                            // lands the socket is connected and silent.
                            Volatile.Write(ref _syncNeed, (int)SyncNeed.Due);
                            if (await SyncAsync(client, authentication, stoppingToken))
                            {
                                Interlocked.CompareExchange(
                                    ref _syncNeed, (int)SyncNeed.None, (int)SyncNeed.Due);
                            }
                            else
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
                        _logger.LogWarning(
                            "The Tradovate trading socket reported an unrecognised state; waiting rather than "
                            + "reconnecting.");
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
                // A transient fault must not stop the application: log, wait a pass, try again.
                _logger.LogWarning(error, "The Tradovate trading connection pass failed; retrying.");
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

    // Any transition INTO Connected re-arms the need, whoever drove it: a fresh connection carries no entity
    // subscription until something syncs it. Assigned rather than escalated, so a connect that lands while an
    // earlier sync is still in flight cannot be cleared by that sync's completion.
    private void OnConnectionStatusChanged(object? sender, ClientModels.ConnectionStatusChange change)
    {
        if (change.IsTradingSocket && change.Current == ClientModels.ConnectionState.Connected)
        {
            Volatile.Write(ref _syncNeed, (int)SyncNeed.Pending);
        }
    }

    // A snapshot landed -- from this host's sync or from the client's own reconnect, which is the whole point of
    // watching the event rather than only tracking what this host sent.
    private void OnSyncCompleted(object? sender, ClientModels.SyncResult result) =>
        Volatile.Write(ref _syncNeed, (int)SyncNeed.None);

    // Returns (null, null) whenever the host must stand down: Tradovate unconfigured (the common, benign case), a
    // client that throws while it is built (bad or absent credentials), or the authentication service missing while
    // the client is present (a wiring defect -- a socket this host connected but could never sync is precisely the
    // silent trading socket it exists to prevent, so it must not connect one).
    private (ITradovateWebSocketClient? Client, IAuthenticationService? Authentication) Resolve()
    {
        try
        {
            if (_services.GetService<ITradovateWebSocketClient>() is not { } client)
            {
                _logger.LogInformation(
                    "Tradovate is not configured in this deployment; the trading connection host is idle.");
                return (null, null);
            }

            if (_services.GetService<IAuthenticationService>() is not { } authentication)
            {
                _logger.LogError(
                    "The Tradovate websocket client is registered but {Service} is not, so the authenticated user "
                    + "id needed by user/syncrequest is unavailable and a connected trading socket could never "
                    + "deliver an order event. The trading connection host will not drive the socket.",
                    nameof(IAuthenticationService));
                return (null, null);
            }

            return (client, authentication);
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Building the Tradovate websocket client failed; the trading connection host is idle and Tradovate "
                + "order, fill and position events will not flow.");
            return (null, null);
        }
    }

    private async Task<bool> ConnectAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectTradingAsync(cancellationToken);
            _logger.LogInformation("Connected the Tradovate trading socket.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error,
                "Connecting the Tradovate trading socket failed; Tradovate order, fill and position events are not "
                + "flowing.");
            return false;
        }
    }

    // Sends user/syncrequest for the authenticated user -- the same id, from the same service, the client's own
    // reconnect uses, so a host-driven sync and a client-driven one subscribe identically. False means "still owed",
    // never "give up": a socket that is up and unsynced is the silent one.
    private async Task<bool> SyncAsync(
        ITradovateWebSocketClient client, IAuthenticationService authentication, CancellationToken cancellationToken)
    {
        try
        {
            if (await authentication.GetUserIdAsync(cancellationToken) is not { } userId)
            {
                // The id comes from the token response, so a later renewal can still supply one -- keep the need
                // armed and stay loud rather than settling into a connected, silent socket.
                _logger.LogError(
                    "Tradovate did not report an authenticated user id, so the trading socket's sync request cannot "
                    + "be sent and no order, fill or position event will arrive. Retrying.");
                return false;
            }

            await client.SyncRequestAsync(
                new ClientModels.SyncRequest { Users = [userId] }, cancellationToken);
            _logger.LogInformation(
                "Synced the Tradovate trading socket; order, fill and position events are flowing.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "The Tradovate trading socket's sync request failed, so it is connected but delivering no order, "
                + "fill or position event. Retrying.");
            return false;
        }
    }

    private TimeSpan NextBackoff(TimeSpan backoff) =>
        backoff < _maxBackoff ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _maxBackoff.Ticks)) : _maxBackoff;
}
