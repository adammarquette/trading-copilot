using MarqSpec.Client.Tradovate;
using MarqSpec.Client.Tradovate.Authentication;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Integration.Tradovate;
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
/// in principle arrive after a fresh connect has re-armed the need.
/// </para>
/// <para>
/// <b>That race is closed here rather than deferred (gh#1051).</b> The state and the rules for clearing it live in
/// <see cref="TradovateTradingSocketSync"/>: a sync this host sends is bound to the connection it was started on and
/// cannot clear a newer one's need, and a completion this host did <i>not</i> send is ignored while one of its own is
/// in flight. What made it look like it needed connection identity from the client was the assumption that both kinds
/// of completion are ambiguous. Only the host's own is: the client raises its reconnect's completion from inside
/// <c>ReconnectAsync</c>, holding the <c>_connectGate</c> that every transition into <c>Connected</c> must also take,
/// so no newer connection can interleave there. Read from the client's source, which is also where the rest of this
/// file's claims about it come from.
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
/// <b>The user id has two sources, because one of them can simply be missing.</b> The id comes from the token
/// response, and <c>IAuthenticationService.GetUserIdAsync</c> returns <see langword="null"/> when the server omitted
/// it — the case the client's own reconnect handles by skipping the sync and reporting <c>Connected</c> anyway. A
/// host that had only that source would loop on it forever over a connected, silent socket, so a null id falls back
/// to <c>GET /auth/me</c> through <c>ITradovateApiClient</c>, which returns the same id from a different endpoint.
/// Only when <b>both</b> come back empty is the sync genuinely unsendable, and that is what escalates.
/// </para>
/// <para>
/// <b>A degradation nobody hears about is the defect, not the degradation.</b> A permanently failing sync — broken
/// credentials, a persistent 4xx, a user id that never arrives — used to leave one <c>LogError</c> at the backoff
/// cadence and nothing else, while the socket reported itself <c>Connected</c> throughout. The operator-facing
/// escalation now lives in <see cref="TradovateSocketConnectionHost"/>, shared with the market-data socket because
/// the shape is not trading-specific, and it <i>reports</i>: this host still never tears a socket down or disables a
/// venue on its own judgement (gh#722, gh#1051).
/// </para>
/// <para>
/// This host does not <i>consume</i> the events it makes flow, and it places nothing: <c>user/syncrequest</c> is a
/// read. What it does now publish is the one fact nothing else could see —
/// <see cref="TradovateTradingSocketSync.IsSynced"/> — so a consumer of order and fill events can fail on a socket
/// that was never subscribed instead of reading it as a quiet account. Execution and the venue's own runtime wiring
/// remain later slices of gh#977.
/// </para>
/// </remarks>
public sealed class TradovateTradingConnectionHost : TradovateSocketConnectionHost
{
    private IAuthenticationService? _authentication;

    private ITradovateApiClient? _apiClient;

    private TradovateTradingSocketSync? _sync;

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
    /// <param name="degradedGrace">How long the socket must not deliver before the operator is told.</param>
    internal TradovateTradingConnectionHost(
        IServiceProvider services,
        ILogger<TradovateTradingConnectionHost> logger,
        TimeSpan pollInterval,
        TimeSpan maxBackoff,
        TimeSpan degradedGrace)
        : base(services, logger, pollInterval, maxBackoff, degradedGrace)
    {
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
    /// <para>
    /// Each of these missing while the client is present is a wiring defect, and the failure direction is the same
    /// for all three: a socket this host connected but could never sync — or could sync while nothing above could
    /// read that it had — is precisely the silent trading socket it exists to prevent, so it must not connect one.
    /// </para>
    /// <para>
    /// All three arrive together. <c>AddTradovateApiClient</c> registers the authentication service, the REST client
    /// and the websocket client in one call, and <c>TradovateTradingSocketSync</c> is registered beside the host
    /// itself, so any of them absent while the websocket client is present is a composition that was edited by hand.
    /// </para>
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

        if (services.GetService<ITradovateApiClient>() is not { } apiClient)
        {
            Logger.LogError(
                "The Tradovate websocket client is registered but {Service} is not, so a token response that "
                + "omitted the user id could not be recovered from /auth/me and the trading socket would stay "
                + "connected and silent for the life of the process. The trading connection host will not drive "
                + "the socket.",
                nameof(ITradovateApiClient));
            return false;
        }

        if (services.GetService<TradovateTradingSocketSync>() is not { } sync)
        {
            Logger.LogError(
                "The Tradovate websocket client is registered but {Register} is not, so nothing above this host "
                + "could tell a synced trading socket from one that was never subscribed, and a silent socket "
                + "would read as a quiet account. The trading connection host will not drive the socket.",
                nameof(TradovateTradingSocketSync));
            return false;
        }

        _authentication = authentication;
        _apiClient = apiClient;
        _sync = sync;
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
    protected override async Task<SocketPassOutcome> SettleAfterHostDrivenConnectAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        Sync.RequireSync();
        return await SyncAsync(client, cancellationToken)
            ? SocketPassOutcome.Delivering
            : SocketPassOutcome.StillOwed;
    }

    /// <inheritdoc />
    protected override async Task<SocketPassOutcome> SettleConnectedSocketAsync(
        ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        switch (Sync.Obligation)
        {
            case TradovateSyncObligation.Pending:
                // Somebody else connected it. The client's own reconnect syncs in the statement after it reports
                // Connected, so give that one pass rather than duplicating a full snapshot; if it never lands, the
                // next pass sends ours.
                //
                // WAITING, not Delivering, and the distinction is the whole reason the outcome is not a bool
                // (gh#1051 review). Nothing is owed on the WIRE this pass, so the cadence must stay at the poll
                // interval rather than backing off -- but this pass has PROVED the socket is unsynced, so it is the
                // last pass that may be read as an all-clear. Reporting it as one let a socket reconnecting faster
                // than the advisory's grace close its own incident and reset the clock forever: connected,
                // degraded, and nobody told.
                Sync.PromoteGraceToDue();
                return SocketPassOutcome.Waiting;

            case TradovateSyncObligation.Due:
                return await SyncAsync(client, cancellationToken)
                    ? SocketPassOutcome.Delivering
                    : SocketPassOutcome.StillOwed;

            case TradovateSyncObligation.None:
                // A snapshot has landed for this connection, so entity frames are flowing. This is the ONLY
                // obligation that may report an all-clear, and it is named rather than defaulted to.
                return SocketPassOutcome.Delivering;

            default:
                // A WHITELIST, not a blacklist, and the difference is safety-relevant here. `Delivering` is the
                // sole input that zeroes the outage clock and CLOSES the operator advisory, so an obligation this
                // switch does not recognise must never reach it: the next member added to this enum is most likely
                // the mid-sync / wedged state gh#1052 needs, and defaulting that to "delivering" would silently
                // resolve `tradovate.socket.degraded:trading` on a socket that has never synced -- the exact state
                // this card exists to report. `Waiting` costs nothing for a value that never occurs: it neither
                // charges the backoff nor clears an outage.
                Logger.LogWarning(
                    "The Tradovate trading socket reported an unrecognised sync obligation; treating it as not "
                    + "delivering rather than assuming the feed is live.");
                return SocketPassOutcome.Waiting;
        }
    }

    // Any transition INTO Connected re-arms the need, whoever drove it: a fresh connection carries no entity
    // subscription until something syncs it. Assigned rather than escalated, and it moves the connection generation
    // with it, so a sync still in flight over the PREVIOUS connection can no longer clear what this one owes.
    private void OnConnectionStatusChanged(object? sender, ClientModels.ConnectionStatusChange change)
    {
        if (change.IsTradingSocket && change.Current == ClientModels.ConnectionState.Connected)
        {
            Sync.OnSocketConnected();
        }
    }

    // A snapshot landed WITHOUT this host asking -- the client's own reconnect syncing itself, which is the whole
    // point of watching the event rather than only tracking what this host sent. A completion arriving while one of
    // this host's own syncs is in flight is left to that sync's connection-bound clear instead (gh#1051).
    private void OnSyncCompleted(object? sender, ClientModels.SyncResult result) => Sync.CompleteObservedSync();

    // Set by TryResolveCollaborators before the loop starts; the loop never runs when that returned false.
    private TradovateTradingSocketSync Sync =>
        _sync ?? throw new InvalidOperationException(
            "The Tradovate trading-socket sync register was not resolved before the trading poll loop started.");

    // Sends user/syncrequest for the authenticated user -- the same id, from the same service, the client's own
    // reconnect uses, so a host-driven sync and a client-driven one subscribe identically. False means "still owed",
    // never "give up": a socket that is up and unsynced is the silent one.
    private async Task<bool> SyncAsync(ITradovateWebSocketClient client, CancellationToken cancellationToken)
    {
        // Captured BEFORE the user id is resolved, not just before the send. Resolving it can run a REST round trip
        // to /auth/me, and a reconnect landing in THAT gap is just as much "this answer is about a connection that
        // no longer exists" as one landing during the send -- so binding only the send would leave the clear below
        // refusing on the "from Due only" rule and logging a cause that was not the cause (gh#1051 review).
        long generation = Sync.Generation;

        if (await ResolveUserIdAsync(cancellationToken) is not { } userId)
        {
            // Both sources came back empty. The id comes from the token response, so a later renewal can still
            // supply one -- keep the need armed and stay loud rather than settling into a connected, silent socket.
            // The shared loop escalates to the operator once this has repeated (gh#1051).
            Logger.LogError(
                "Tradovate reported no authenticated user id from either the token response or /auth/me, so the "
                + "trading socket's sync request cannot be sent and no order, fill or position event will arrive. "
                + "Retrying.");
            return false;
        }

        // Marks a HOST sync in flight, so a completion raised while it runs is left to the connection-bound clear
        // below rather than taken at face value by the event handler. Its return is DISCARDED on purpose: it is the
        // generation as of now, and the capture above -- taken before the user-id read, which can run a REST round
        // trip -- is deliberately the older of the two. Binding to the earlier one is what makes a reconnect during
        // that read refuse the clear; binding to this one would silently accept it.
        Sync.BeginHostSync();
        try
        {
            await client.SyncRequestAsync(
                new ClientModels.SyncRequest { Users = [userId] }, cancellationToken);
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
        finally
        {
            // In a finally, and before the clear below: leaving the marker set would suppress every later
            // client-driven completion for the rest of the process's life.
            Sync.EndHostSync();
        }

        if (!Sync.CompleteHostSync(generation))
        {
            Logger.LogWarning(
                "The Tradovate trading socket's sync snapshot arrived for a connection that no longer exists, so "
                + "the current one is still unsynced and silent. Retrying.");
            return false;
        }

        Logger.LogInformation(
            "Synced the Tradovate trading socket; order, fill and position events are flowing.");
        return true;
    }

    // The token response is the primary source and /auth/me is the fallback, because the primary can simply be
    // absent: GetUserIdAsync returns null when the server omitted the id, which is the case the client's own
    // reconnect answers by skipping its sync and reporting Connected anyway. A host with one source would loop on
    // that forever over a connected, silent socket (gh#1051).
    private async Task<long?> ResolveUserIdAsync(CancellationToken cancellationToken)
    {
        // Set by TryResolveCollaborators before the loop starts; the loop never runs when that returned false.
        IAuthenticationService authentication = _authentication ?? throw new InvalidOperationException(
            "The Tradovate authentication service was not resolved before the trading poll loop started.");
        ITradovateApiClient apiClient = _apiClient ?? throw new InvalidOperationException(
            "The Tradovate REST client was not resolved before the trading poll loop started.");

        try
        {
            if (await authentication.GetUserIdAsync(cancellationToken) is { } userId)
            {
                return userId;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Not fatal to the pass: the fallback below reads the id from a different endpoint, so a token service
            // that is failing does not have to mean an unsyncable socket.
            Logger.LogWarning(
                error, "Reading the Tradovate user id from the token response failed; trying /auth/me.");
        }

        try
        {
            ClientModels.AuthMe me = await apiClient.GetAuthMeAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(me.ErrorText))
            {
                // Tradovate reports REST failures in the body rather than the status, so a 200 carrying ErrorText
                // is a failure -- and the UserId beside it is not to be trusted.
                Logger.LogWarning(
                    "Tradovate's /auth/me reported an error while recovering the user id for the trading socket's "
                    + "sync request.");
                return null;
            }

            if (me.UserId is { } fallbackUserId)
            {
                Logger.LogWarning(
                    "Tradovate's token response carried no user id, so the trading socket's sync request used the "
                    + "id from /auth/me instead. The socket is synced; the token response is the anomaly.");
                return fallbackUserId;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Logger.LogWarning(
                error, "Recovering the Tradovate user id from /auth/me failed; the trading socket stays unsynced.");
        }

        return null;
    }
}
