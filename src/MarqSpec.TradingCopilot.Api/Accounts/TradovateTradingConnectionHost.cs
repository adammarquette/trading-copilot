using MarqSpec.Client.Tradovate.Authentication;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Venues;
using Microsoft.Extensions.DependencyInjection;
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
/// <b>The loop is not this class's.</b> Connect, backoff and its reset, the <c>Connecting</c>/<c>Reconnecting</c>
/// wait, the unrecognised-state default, containment and clean exit all live in
/// <see cref="TradovateSocketConnectionHost"/>, shared with the market-data socket's host. They used to be two
/// hand-maintained copies, and within a week they disagreed about whether a successful connect resets the backoff
/// (gh#1054). What is left here is what genuinely differs: <b>this socket's post-connect obligation</b>, and the
/// <c>ConnectionStatusChanged</c> re-arm that only this socket needs.
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
/// <b>Why this host watches <c>ConnectionStatusChanged</c> and the market-data host does not.</b> This socket can
/// reach <c>Connected</c> <i>unsynced</i> without anything failing — the client's reconnect skips its own sync on a
/// null user id and reports <c>Connected</c> anyway — and the transition is the only cue that a live connection
/// carries no entity subscription. The market-data socket has no equivalent state: a replay failure propagates and
/// parks it in <c>Disconnected</c>, where the shared poll picks it up. The asymmetry is deliberate, and flattening
/// it into the shared loop would put a duplicate full snapshot on the ordinary path every time a quote feed blipped.
/// </para>
/// <para>
/// This host does not <i>consume</i> the events it makes flow, and it places nothing: <c>user/syncrequest</c> is a
/// read. Translating Tradovate's order / position / fill entities onto the neutral account-event seam is the next
/// slice of gh#977, as is the venue's own runtime wiring.
/// </para>
/// </remarks>
public sealed class TradovateTradingConnectionHost : TradovateSocketConnectionHost
{
    // Written from the client's event handlers (arbitrary threads) as well as the loop, so every read and write goes
    // through Interlocked/Volatile. Starts at Pending rather than None: a socket already Connected when this host
    // starts was synced by nothing this process can see, and a possibly-duplicate snapshot is the right way to be
    // wrong about that -- a silent trading socket is not.
    private int _syncNeed = (int)SyncNeed.Pending;

    private IAuthenticationService? _authentication;

    /// <summary>Creates the host with the production cadence.</summary>
    /// <param name="services">The root provider — the client and its authentication service resolve from it lazily.</param>
    /// <param name="logger">The logger.</param>
    public TradovateTradingConnectionHost(
        IServiceProvider services, ILogger<TradovateTradingConnectionHost> logger)
        : base(services, logger)
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
        : base(services, logger, pollInterval, maxBackoff)
    {
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
    protected override string SocketName => "trading";

    /// <inheritdoc />
    protected override string SilenceConsequence =>
        "Tradovate order, fill and position events are not flowing";

    /// <inheritdoc />
    protected override ClientModels.ConnectionState ReadState(ITradovateWebSocketClient client) =>
        client.TradingState;

    /// <inheritdoc />
    protected override Task ConnectSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken) =>
        client.ConnectTradingAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The authentication service missing while the client is present is a wiring defect: a socket this host
    /// connected but could never sync is precisely the silent trading socket it exists to prevent, so it must not
    /// connect one.
    /// </remarks>
    protected override bool TryResolveCollaborators(IServiceProvider services)
    {
        if (services.GetService<IAuthenticationService>() is not { } authentication)
        {
            Logger.LogError(
                "The Tradovate websocket client is registered but {Service} is not, so the authenticated user "
                + "id needed by user/syncrequest is unavailable and a connected trading socket could never "
                + "deliver an order event. The trading connection host will not drive the socket.",
                nameof(IAuthenticationService));
            return false;
        }

        _authentication = authentication;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Trading-only. The market-data socket needs no equivalent: its replay failure propagates and parks that socket
    /// in <c>Disconnected</c>, so the shared poll already sees it.
    /// </remarks>
    protected override IDisposable Observe(ITradovateWebSocketClient client)
    {
        // Attached BEFORE the first pass, so a connect that lands between the resolve and the loop is still seen.
        client.ConnectionStatusChanged += OnConnectionStatusChanged;
        client.SyncCompleted += OnSyncCompleted;
        return new Detach(() =>
        {
            client.ConnectionStatusChanged -= OnConnectionStatusChanged;
            client.SyncCompleted -= OnSyncCompleted;
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// A host-driven connect authorizes the socket and sends nothing else, so the sync is owed with <b>no grace</b>:
    /// nothing in the process will ever send it otherwise, and until it lands the socket is connected and silent.
    /// </remarks>
    protected override async Task<bool> SettleAfterHostDrivenConnectAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _syncNeed, (int)SyncNeed.Due);
        if (!await SyncAsync(client, cancellationToken))
        {
            return false;
        }

        // Only from Due: a drop that re-armed the need mid-sync must not be cleared here.
        Interlocked.CompareExchange(ref _syncNeed, (int)SyncNeed.None, (int)SyncNeed.Due);
        return true;
    }

    /// <inheritdoc />
    protected override async Task<bool> SettleConnectedSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        switch ((SyncNeed)Volatile.Read(ref _syncNeed))
        {
            case SyncNeed.Pending:
                // Somebody else connected it. The client's own reconnect syncs in the statement after it reports
                // Connected, so give that one pass rather than duplicating a full snapshot; if it never lands, the
                // next pass sends ours. Nothing is owed on the WIRE this pass, so the cadence stays at the poll
                // interval rather than backing off.
                Interlocked.CompareExchange(ref _syncNeed, (int)SyncNeed.Due, (int)SyncNeed.Pending);
                return true;

            case SyncNeed.Due:
                if (!await SyncAsync(client, cancellationToken))
                {
                    return false;
                }

                // Only from Due: a drop that re-armed the need mid-sync must not be cleared here.
                Interlocked.CompareExchange(ref _syncNeed, (int)SyncNeed.None, (int)SyncNeed.Due);
                return true;

            default:
                return true;
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

    // Sends user/syncrequest for the authenticated user -- the same id, from the same service, the client's own
    // reconnect uses, so a host-driven sync and a client-driven one subscribe identically. False means "still owed",
    // never "give up": a socket that is up and unsynced is the silent one.
    private async Task<bool> SyncAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        // Set by TryResolveCollaborators before the loop starts; the loop never runs when that returned false.
        IAuthenticationService authentication = _authentication ?? throw new InvalidOperationException(
            "The Tradovate authentication service was not resolved before the trading poll loop started.");

        try
        {
            if (await authentication.GetUserIdAsync(cancellationToken) is not { } userId)
            {
                // The id comes from the token response, so a later renewal can still supply one -- keep the need
                // armed and stay loud rather than settling into a connected, silent socket.
                Logger.LogError(
                    "Tradovate did not report an authenticated user id, so the trading socket's sync request cannot "
                    + "be sent and no order, fill or position event will arrive. Retrying.");
                return false;
            }

            await client.SyncRequestAsync(
                new ClientModels.SyncRequest { Users = [userId] }, cancellationToken);
            Logger.LogInformation(
                "Synced the Tradovate trading socket; order, fill and position events are flowing.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Logger.LogError(
                error,
                "The Tradovate trading socket's sync request failed, so it is connected but delivering no order, "
                + "fill or position event. Retrying.");
            return false;
        }
    }
}
