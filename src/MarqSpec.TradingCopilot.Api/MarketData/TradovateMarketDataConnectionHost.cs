using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using Microsoft.Extensions.DependencyInjection;
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
/// <b>The loop is not this class's.</b> Connect, backoff and its reset, the <c>Connecting</c>/<c>Reconnecting</c>
/// wait, the unrecognised-state default, containment and clean exit all live in
/// <see cref="TradovateSocketConnectionHost"/>, shared with the trading socket's host — because when they were two
/// hand-maintained copies they diverged on the backoff reset within a week (gh#1054). What is left here is the one
/// thing that genuinely differs: <b>what has to happen after a connect</b>.
/// </para>
/// <para>
/// <b>Why a host owns this and no read path does.</b> <c>ConnectMarketDataAsync</c> is not idempotent: it tears
/// the transport down and rebuilds it. A bars read or a quote stream calling it mid-session would silently destroy
/// another consumer's subscriptions, so those paths refuse while the socket is down and this host is the single
/// writer of its lifecycle.
/// </para>
/// <para>
/// <b>The gap this host's own obligation closes.</b> The client recovers on its own from a dropped or stalled
/// socket, and that path replays subscriptions — but that recovery <b>gives up after a single failed attempt</b>,
/// parking the socket in <see cref="ClientModels.ConnectionState.Disconnected"/> for the rest of the session; and
/// the manual connect that is the only way out of that state does <b>not</b> replay. So a connect this host drove
/// is always followed by a resubscribe from <see cref="TradovateQuoteSubscriptions"/>. Without the replay a
/// recovered socket comes back connected-but-silent, with every open stream alive and never ticking — which is what
/// stalls a hidden stop's promotion, and it raises nothing.
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
/// <c>_owed</c>; and a contract whose last holder releases and whose newcomer acquires between the
/// <see cref="TradovateQuoteSubscriptions.LiveKeys"/> snapshot and its resubscribe sends both. What the gateway does
/// with a duplicate <c>md/subscribeQuote</c> is unverified from this side — nothing in the vendored client pins it —
/// so it is a staging observation to make once credentials exist, not an assumption to build on.
/// </para>
/// <para>
/// <b>Why this host watches no client event, and the trading host does.</b> There is no
/// <c>Connected</c>-but-unsubscribed state for this socket to reach silently: the client's own reconnect replays,
/// and a replay that fails propagates out of <c>ConnectUnlockedAsync</c> and parks the socket in
/// <c>Disconnected</c>, where the poll picks it up. The trading socket can reach <c>Connected</c> unsynced
/// <i>without</i> failing (a null user id), which is why it — and only it — re-arms on
/// <c>ConnectionStatusChanged</c>. The asymmetry is deliberate, not an omission.
/// </para>
/// </remarks>
public sealed class TradovateMarketDataConnectionHost : TradovateSocketConnectionHost
{
    // Carried across passes: the keys still owed a wire subscription. A replay that could not finish must be retried
    // while the socket looks healthy, because nothing else would ever revisit a Connected socket that is subscribed
    // to nothing. Tracked PER KEY rather than as one flag, so a key that keeps failing is the only thing still
    // retried -- re-sending the ones that already succeeded would rest on the gateway treating a duplicate subscribe
    // as a harmless no-op, which is not something this side can know. That keeps duplicates off the ordinary path;
    // the two edge paths that can still produce one are in this type's remarks.
    private readonly HashSet<string> _owed = new(StringComparer.Ordinal);

    private TradovateQuoteSubscriptions? _subscriptions;

    /// <summary>Creates the host with the production cadence.</summary>
    /// <param name="services">The root provider — the client and the subscription register resolve from it lazily.</param>
    /// <param name="logger">The logger.</param>
    public TradovateMarketDataConnectionHost(
        IServiceProvider services, ILogger<TradovateMarketDataConnectionHost> logger)
        : base(services, logger)
    {
    }

    /// <summary>Creates the host with an explicit cadence, so a test does not wait out the production delays.</summary>
    /// <param name="services">The root provider.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pollInterval">How often the socket's state is sampled.</param>
    /// <param name="maxBackoff">The ceiling the backoff doubles up to.</param>
    /// <param name="degradedGrace">How long the socket must not deliver before the operator is told.</param>
    /// <param name="delayAsync">The between-pass wait; the production wait when null (gh#1070).</param>
    internal TradovateMarketDataConnectionHost(
        IServiceProvider services,
        ILogger<TradovateMarketDataConnectionHost> logger,
        TimeSpan pollInterval,
        TimeSpan maxBackoff,
        TimeSpan degradedGrace,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        : base(services, logger, pollInterval, maxBackoff, degradedGrace, delayAsync)
    {
    }

    /// <inheritdoc />
    protected override string SocketName => "market-data";

    /// <inheritdoc />
    protected override string SilenceConsequence => "Tradovate market data is not flowing";

    /// <inheritdoc />
    protected override ClientModels.ConnectionState ReadState(ITradovateWebSocketClient client) =>
        client.MarketDataState;

    /// <inheritdoc />
    protected override Task ConnectSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken) =>
        client.ConnectMarketDataAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The register missing while the client is present is a wiring defect: replaying from a register nothing writes
    /// to would resubscribe nothing at all, so refuse to drive the socket rather than pretend to guard it.
    /// </remarks>
    protected override bool TryResolveCollaborators(IServiceProvider services)
    {
        if (services.GetService<TradovateQuoteSubscriptions>() is not { } subscriptions)
        {
            Logger.LogError(
                "The Tradovate websocket client is registered but {Register} is not, so a reconnect could not "
                + "restore any quote subscription. The market-data connection host will not drive the socket.",
                nameof(TradovateQuoteSubscriptions));
            return false;
        }

        _subscriptions = subscriptions;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A host-driven connect takes the client's non-replaying path, so the socket is now subscribed to nothing:
    /// EVERY live key is owed again, whatever was owed before.
    /// </remarks>
    protected override async Task<SocketPassOutcome> SettleAfterHostDrivenConnectAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        _owed.Clear();
        _owed.UnionWith(Subscriptions.LiveKeys);
        await ReplayAsync(client, cancellationToken);
        return Outcome();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A socket that is up and fully subscribed owes nothing, which is the ordinary case. What survives here is the
    /// tail of a partial replay: those keys are retried while the socket looks healthy, because nothing else would.
    /// </remarks>
    protected override async Task<SocketPassOutcome> SettleConnectedSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        if (_owed.Count > 0)
        {
            await ReplayAsync(client, cancellationToken);
        }

        return Outcome();
    }

    // This socket has no third case. Unlike the trading socket's grace pass, every pass here either leaves the
    // register fully replayed -- the feed is live -- or leaves keys that were attempted on the wire and failed,
    // which is what the backoff exists for. `Waiting` is deliberately unreachable rather than defensively returned:
    // a market-data pass that owes nothing genuinely is an all-clear.
    private SocketPassOutcome Outcome() =>
        _owed.Count == 0 ? SocketPassOutcome.Delivering : SocketPassOutcome.StillOwed;

    // Set by TryResolveCollaborators before the loop starts; the loop never runs when that returned false.
    private TradovateQuoteSubscriptions Subscriptions =>
        _subscriptions ?? throw new InvalidOperationException(
            "The Tradovate quote-subscription register was not resolved before the market-data poll loop started.");

    // Resubscribes the keys still owed one, and leaves in `_owed` exactly those that still are. A key that fails
    // does NOT abort the pass: aborting would let one persistently failing contract starve every key behind it,
    // reproducing -- for the tail of the register -- the very connected-but-silent feed this host exists to prevent.
    // Each subscribe goes through the register, so it is serialized against a stream starting or ending on the same
    // contract, and a key whose stream ended while the socket was down is skipped rather than replayed into a
    // channel with no reader.
    private async Task ReplayAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        List<string> failed = [];
        int replayed = 0;

        foreach (string key in _owed.ToArray())
        {
            try
            {
                if (await Subscriptions.ResubscribeAsync(
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
                Logger.LogError(
                    error,
                    "Replaying the Tradovate quote subscription for contract {Contract} onto the reconnected "
                    + "market-data socket failed; that contract is not streaming. Retrying on the next pass.",
                    key);
            }
        }

        _owed.Clear();
        _owed.UnionWith(failed);

        if (replayed > 0)
        {
            Logger.LogInformation(
                "Replayed {Count} Tradovate quote subscription(s) onto the reconnected market-data socket.",
                replayed);
        }
    }
}
