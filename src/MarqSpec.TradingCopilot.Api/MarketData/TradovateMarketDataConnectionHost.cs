using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Owns the lifecycle of the process-wide Tradovate <b>market-data socket</b> (R-17, gh#977): it connects the
/// socket at startup, drives it back up when the client has given up on it, and <b>replays every live quote
/// subscription</b> after a connect it drove. Idle — not a failure — in a deployment where Tradovate is not
/// configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a host owns this and no read path does.</b> <c>ConnectMarketDataAsync</c> is not idempotent: it tears
/// the transport down and rebuilds it. A bars read or a quote stream calling it mid-session would silently destroy
/// another consumer's subscriptions, so those paths refuse while the socket is down and this host is the single
/// writer of its lifecycle.
/// </para>
/// <para>
/// <b>The two gaps it closes.</b> The client recovers on its own from a dropped or stalled socket, and that path
/// replays subscriptions — so while the client reports <see cref="ClientModels.ConnectionState.Connecting"/> or
/// <see cref="ClientModels.ConnectionState.Reconnecting"/> this host waits rather than racing it. But that
/// recovery <b>gives up after a single failed attempt</b>, parking the socket in
/// <see cref="ClientModels.ConnectionState.Disconnected"/> for the rest of the session; and the manual connect
/// that is the only way out of that state does <b>not</b> replay. So this host retries with backoff from
/// <c>Disconnected</c>, and resubscribes from <see cref="TradovateQuoteSubscriptions"/> afterwards. Without the
/// replay a recovered socket comes back connected-but-silent, with every open stream alive and never ticking —
/// which is what stalls a hidden stop's promotion, and it raises nothing.
/// </para>
/// <para>
/// <b>A failed replay is remembered, per key, and backed off.</b> "Connected" looks healthy, so a pass that
/// half-resubscribed would never be revisited; the keys still owed a subscription therefore survive into the next
/// pass until each lands. One key's failure never aborts the pass — that would let a persistently failing key starve
/// every key behind it, which is the same silent feed in miniature — and only the failures are retried, so the
/// ordinary path no longer depends on the gateway treating a duplicate subscribe as a harmless no-op. A pass that
/// still owes something backs off exactly as a failed connect does, since the usual reason a replay fails — a rate
/// limit — is one that retrying at full cadence would sustain.
/// </para>
/// <para>
/// <b>A duplicate subscribe is rare here, not impossible.</b> Two edge paths can still emit one, and neither is
/// worth contorting the design to remove: the client records a key <i>before</i> its own subscribe throws, so a key
/// this host failed to replay is still replayed by the client's next internal reconnect and then again from
/// <c>owed</c>; and a contract whose last holder releases and whose newcomer acquires between the
/// <see cref="TradovateQuoteSubscriptions.LiveKeys"/> snapshot and its resubscribe sends both. What the gateway does
/// with a duplicate <c>md/subscribeQuote</c> is unverified from this side — nothing in the vendored client pins it —
/// so it is a staging observation to make once credentials exist, not an assumption to build on.
/// </para>
/// <para>
/// <b>Everything is caught.</b> Under the default <c>BackgroundServiceExceptionBehavior.StopHost</c> an exception
/// escaping <see cref="ExecuteAsync"/> stops the whole application — the auto-flatten watchdog and the kill switch
/// with it. Missing Tradovate credentials are enough to throw while the client is constructed, so the failure
/// direction here is always "this venue's feed degrades", never "the platform will not run" (engineering §9).
/// </para>
/// <para>
/// The <b>trading</b> socket's lifecycle is not this host's concern — <c>TradovateTradingConnectionHost</c> owns it
/// (gh#977). The two are deliberately separate loops rather than one template: what has to happen after a connect
/// differs in kind, a per-key replay that must survive partial failure here versus a single <c>user/syncrequest</c>
/// there. The cost of that choice is that a fix to one loop's shape has to be carried to the other by hand.
/// </para>
/// </remarks>
public sealed class TradovateMarketDataConnectionHost : BackgroundService
{
    /// <summary>How often the socket's state is sampled, and the first delay before a retried connect.</summary>
    private static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling the connect backoff doubles up to, so a long outage is retried without hammering it.</summary>
    private static TimeSpan DefaultMaxBackoff { get; } = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<TradovateMarketDataConnectionHost> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;

    /// <summary>Creates the host with the production cadence.</summary>
    /// <param name="services">The root provider — the client and the subscription register resolve from it lazily.</param>
    /// <param name="logger">The logger.</param>
    public TradovateMarketDataConnectionHost(
        IServiceProvider services, ILogger<TradovateMarketDataConnectionHost> logger)
        : this(services, logger, DefaultPollInterval, DefaultMaxBackoff)
    {
    }

    /// <summary>Creates the host with an explicit cadence, so a test does not wait out the production delays.</summary>
    /// <param name="services">The root provider.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pollInterval">How often the socket's state is sampled.</param>
    /// <param name="maxBackoff">The ceiling the connect backoff doubles up to.</param>
    internal TradovateMarketDataConnectionHost(
        IServiceProvider services,
        ILogger<TradovateMarketDataConnectionHost> logger,
        TimeSpan pollInterval,
        TimeSpan maxBackoff)
    {
        _services = services;
        _logger = logger;
        _pollInterval = pollInterval;
        _maxBackoff = maxBackoff;
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
                "The Tradovate market-data connection host stopped unexpectedly; Tradovate market data will not "
                + "recover until the process restarts.");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Resolve LAZILY, inside the run: constructing the client touches credentials the process (and every test
        // host) may lack at startup, and an eager injection would crash the host instead of degrading this feed.
        (ITradovateWebSocketClient? client, TradovateQuoteSubscriptions? subscriptions) = Resolve();
        if (client is null || subscriptions is null)
        {
            return;
        }

        // Carried across passes: the keys still owed a wire subscription. A replay that could not finish must be
        // retried while the socket looks healthy, because nothing else would ever revisit a Connected socket that is
        // subscribed to nothing. Tracked PER KEY rather than as one flag, so a key that keeps failing is the only
        // thing still retried -- re-sending the ones that already succeeded would rest on the gateway treating a
        // duplicate subscribe as a harmless no-op, which is not something this side can know. That keeps duplicates
        // off the ordinary path; the two edge paths that can still produce one are in this type's remarks.
        HashSet<string> owed = new(StringComparer.Ordinal);
        TimeSpan backoff = _pollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = _pollInterval;

            try
            {
                switch (client.MarketDataState)
                {
                    case ClientModels.ConnectionState.Connected:
                        if (owed.Count > 0)
                        {
                            await ReplayAsync(client, subscriptions, owed, stoppingToken);
                        }

                        // Back off while anything is still owed. A replay usually fails for the same reason a
                        // connect does -- a rate limit above all -- and retrying every poll interval would keep the
                        // venue refusing the very subscribes the feed is waiting on. A pass that owes nothing resets.
                        if (owed.Count > 0)
                        {
                            delay = backoff;
                            backoff = NextBackoff(backoff);
                        }
                        else
                        {
                            backoff = _pollInterval;
                        }

                        break;

                    case ClientModels.ConnectionState.Connecting:
                    case ClientModels.ConnectionState.Reconnecting:
                        // The client is mid-attempt, and its own reconnect replays subscriptions. Driving a manual
                        // connect now would tear that attempt down and land on the non-replaying path instead.
                        break;

                    case ClientModels.ConnectionState.Disconnected:
                        if (await ConnectAsync(client, stoppingToken))
                        {
                            // A host-driven connect takes the client's non-replaying path, so the socket is now
                            // subscribed to nothing: EVERY live key is owed again, whatever was owed before.
                            owed.Clear();
                            owed.UnionWith(subscriptions.LiveKeys);
                            await ReplayAsync(client, subscriptions, owed, stoppingToken);
                            backoff = _pollInterval;
                            if (owed.Count > 0)
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
                            "The Tradovate market-data socket reported an unrecognised state; waiting rather than "
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
                _logger.LogWarning(error, "The Tradovate market-data connection pass failed; retrying.");
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

    // Returns (null, null) whenever the host must stand down: Tradovate unconfigured (the common, benign case), a
    // client that throws while it is built (bad or absent credentials), or the register missing while the client is
    // present (a wiring defect -- replaying from a register nothing writes to would resubscribe nothing at all).
    private (ITradovateWebSocketClient? Client, TradovateQuoteSubscriptions? Subscriptions) Resolve()
    {
        try
        {
            if (_services.GetService<ITradovateWebSocketClient>() is not { } client)
            {
                _logger.LogInformation(
                    "Tradovate is not configured in this deployment; the market-data connection host is idle.");
                return (null, null);
            }

            if (_services.GetService<TradovateQuoteSubscriptions>() is not { } subscriptions)
            {
                _logger.LogError(
                    "The Tradovate websocket client is registered but {Register} is not, so a reconnect could not "
                    + "restore any quote subscription. The market-data connection host will not drive the socket.",
                    nameof(TradovateQuoteSubscriptions));
                return (null, null);
            }

            return (client, subscriptions);
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Building the Tradovate websocket client failed; the market-data connection host is idle and "
                + "Tradovate market data will not flow.");
            return (null, null);
        }
    }

    private async Task<bool> ConnectAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectMarketDataAsync(cancellationToken);
            _logger.LogInformation("Connected the Tradovate market-data socket.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error, "Connecting the Tradovate market-data socket failed; Tradovate market data is not flowing.");
            return false;
        }
    }

    private TimeSpan NextBackoff(TimeSpan backoff) =>
        backoff < _maxBackoff ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _maxBackoff.Ticks)) : _maxBackoff;

    // Resubscribes the keys still owed one, and leaves in <paramref name="owed"/> exactly those that still are.
    // A key that fails does NOT abort the pass: aborting would let one persistently failing key starve every key
    // behind it, reproducing -- for the tail of the register -- the very connected-but-silent feed this host exists
    // to prevent. Each subscribe goes through the register, so it is serialized against a stream starting or ending
    // on the same contract, and a key whose stream ended while the socket was down is skipped rather than replayed
    // into a channel with no reader.
    private async Task ReplayAsync(
        ITradovateWebSocketClient client,
        TradovateQuoteSubscriptions subscriptions,
        HashSet<string> owed,
        CancellationToken cancellationToken)
    {
        List<string> failed = [];
        int replayed = 0;

        foreach (string key in owed.ToArray())
        {
            try
            {
                if (await subscriptions.ResubscribeAsync(
                        key, token => client.SubscribeQuoteAsync(key, token), cancellationToken))
                {
                    replayed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                failed.Add(key);
                _logger.LogError(
                    error,
                    "Replaying the Tradovate quote subscription for contract {Contract} onto the reconnected "
                    + "market-data socket failed; that contract is not streaming. Retrying on the next pass.",
                    key);
            }
        }

        owed.Clear();
        owed.UnionWith(failed);

        if (replayed > 0)
        {
            _logger.LogInformation(
                "Replayed {Count} Tradovate quote subscription(s) onto the reconnected market-data socket.",
                replayed);
        }
    }
}
