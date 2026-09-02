using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The Tradovate account-event seam (R-17, gh#977): the trading socket's <c>props</c> entity frames behind
/// <see cref="IAccountEventStream"/>, translated onto the neutral <see cref="AccountEvent"/>s at this boundary so no
/// vendor type crosses into the core. A <b>singleton</b> over the process's one websocket client, exactly as
/// <see cref="TradovateConnection"/> is (one credential set per process, ADR-0015).
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not own the socket, and must not.</b> <c>ConnectTradingAsync</c> is the <i>manual</i> connect path: it
/// opens the transport, authorizes it, and sends no <c>user/syncrequest</c> — and Tradovate pushes <c>props</c> frames
/// only to a socket that has synced. Connecting from here would therefore produce a socket that is connected,
/// authorized and permanently silent. <c>TradovateTradingConnectionHost</c> owns that lifecycle (gh#1048); this seam
/// refuses loudly when the socket is down and lets the caller's supervisor re-subscribe, the same shape the bars and
/// quote reads already take.
/// </para>
/// <para>
/// <b>Why a socket drop ends the stream.</b> A dropped socket delivers nothing further, and the client replays
/// subscriptions only on its <i>own</i> reconnect — which gives up after one attempt. An open sequence over a dead
/// socket is not distinguishable from a quiet account by anything above it, and a quiet account is exactly what
/// auto-flatten must never be told (R-13, ADR-0019). So a transition off <c>Connected</c> on the <b>trading</b>
/// socket completes the channel and the enumeration then throws, after delivering whatever was already buffered —
/// those events are still truth. The caller re-subscribes over the socket the connection host brings back and
/// re-syncs. The market-data socket's transitions are ignored: a quote-feed blip is not this stream's business.
/// </para>
/// <para>
/// <b>Attribution, and why this seam is stateful where ProjectX's is not.</b> A ProjectX trade notification carries
/// its own account, so that adapter maps one payload to one event and holds nothing. A Tradovate <c>fill</c> entity
/// carries only an <c>orderId</c> — no account at all — so the account has to come from the <c>order</c> entity, and
/// this stream keeps the order → account map that joins them. Nothing orders the frames, so a fill can arrive before
/// the order that names it: such a fill is <b>held</b>, not dropped, and released the moment an order (or a sync
/// snapshot) supplies its account. Dropping it would lose a real execution the journal cannot reconstruct.
/// </para>
/// <para>
/// <b>The sync snapshot: orders seed, fills are emitted, positions are dropped.</b> <c>user/syncrequest</c> returns a
/// full re-delivery of the session's orders, fills and positions, and the connection host sends one after every
/// connect it drives. The three are not interchangeable, so they are not treated alike.
/// </para>
/// <para>
/// Its <b>fills are emitted</b>, because a fill that executed while the socket was down exists <i>nowhere else</i> —
/// live <c>props</c> frames carry changes from the sync point forward, and the reconciliation path (gh#193) recovers
/// positions, not fills. Losing one means no <c>Fill</c> row, no <c>Trade</c>, and a real realized loss that never
/// reaches the R-5 governor, the R-9 window or the R-4 throttle. Re-delivery is safe because a fill is idempotent by
/// construction downstream: the unique index on <c>{ OrderId, VenueFillKey }</c> makes a replay a skip.
/// </para>
/// <para>
/// Its <b>positions are dropped</b>, and that asymmetry is the point. A position event is <i>not</i> idempotent
/// downstream: a flat re-drives the OCO-exit retire and composes a round trip, so a re-delivered one double-counts
/// realized P&amp;L into the very gate the fills above must reach accurately. Its <b>orders</b> only seed the
/// order → account map, which is the one thing nothing else can supply — the account behind an order this process
/// never saw a <c>props</c> frame for.
/// </para>
/// <para>
/// <b>What this stream does not decide.</b> It holds no limit and makes no enforcement decision — it translates, and
/// the risk / execution gate below the model enforces (ADR-0007). It also grants no account: an event for an account
/// this process did not subscribe is discarded here, because one Tradovate login syncs <i>every</i> account the user
/// holds (unlike ProjectX, which subscribes per account), so without that filter another account's orders would reach
/// this process's journal.
/// </para>
/// <para>
/// <b>Connected is not the same as delivering (gh#1051).</b> Tradovate pushes <c>props</c> frames only to a socket
/// that has completed <c>user/syncrequest</c>, and a socket that is open, authorized and unsynced reports
/// <c>Connected</c> throughout while delivering nothing at all. So this stream's silence is only evidence about the
/// account when the socket underneath it was actually synced, and
/// <see cref="TradovateTradingSocketSync"/> is what makes that knowable at all. The register is <b>required</b>,
/// never optional — a seam that accepted a null and defaulted to "assume synced" would be the guard that cannot
/// fail on the thing it names.
/// </para>
/// <para>
/// <b>And yet this stream does NOT refuse an unsynced socket — that would be the worse bug.</b>
/// <see cref="TradovateTradingSocketSync.IsSynced"/> can only become true <i>after</i> <c>SyncCompleted</c> has
/// already been raised: the client raises it synchronously from inside <c>SyncRequestAsync</c> before that call
/// returns, and every path that clears the obligation runs from inside that same invocation list. A stream gated on
/// <c>IsSynced</c> could therefore only ever attach on the far side of the snapshot, which makes <c>OnSync</c>
/// <b>unreachable</b> — and <c>OnSync</c> is the sole source of the order → account seed for every order predating
/// the connect, and of the fills that executed while the socket was down, which exist nowhere else. Every
/// transition off <c>Connected</c> ends the stream, so a re-subscribed one would meet the same wall: the loss would
/// be permanent rather than occasional. Refusing here would reintroduce, through this card's own fix, the exact
/// harm it cites as its motivation.
/// </para>
/// <para>
/// <b>What is guarded instead.</b> Two things, neither of which costs the snapshot. The stream ends when the
/// <i>connection</i> under it is replaced while it is opening — the generation, not the sync state, because a new
/// connection is legitimately unsynced for a moment and only the generation distinguishes that from a drop nobody
/// saw. And when the stream ends having opened unsynced and never seen a snapshot, it says so: its silence was not
/// evidence about the account. A socket that stays up and never syncs is reported by
/// <c>TradovateSocketConnectionHost</c>'s operator advisory, which is the other half of gh#1051 — the alarm belongs
/// where something can act on it, not in an exception thrown at a supervisor whose only move is to re-subscribe
/// into the same state.
/// </para>
/// </remarks>
public sealed class TradovateAccountEventStream : IAccountEventStream
{
    /// <summary>
    /// How many fills may wait for the order that names their account before the oldest is dropped. Generous by
    /// design: the wait is a frame-ordering race measured in milliseconds, not a backlog, so reaching this bound
    /// means something is wrong rather than busy — and it is logged as an error, because the fill being dropped is a
    /// real execution nothing downstream can reconstruct.
    /// </summary>
    private const int MaxHeldFills = 512;

    private readonly ITradovateWebSocketClient _webSocket;
    private readonly TradovateTradingSocketSync _sync;
    private readonly ILogger<TradovateAccountEventStream> _logger;

    /// <summary>Creates the seam over the process's realtime client.</summary>
    /// <param name="webSocket">The Tradovate dual-socket client (a singleton — the trading socket is process-wide).</param>
    /// <param name="sync">
    /// The process-wide record of whether the trading socket has actually been synced. Required, not optional: a
    /// default of "assume synced" would make this seam report an unsynced socket as a quiet account, which is the
    /// exact failure it is being given this dependency to prevent (gh#1051).
    /// </param>
    /// <param name="logger">
    /// The logger. Required, not optional: every case this seam cannot resolve — an unattributable fill, a position
    /// it had to refuse — leaves no other trace, and a swallow nobody can see is the silence it exists to prevent.
    /// </param>
    public TradovateAccountEventStream(
        ITradovateWebSocketClient webSocket,
        TradovateTradingSocketSync sync,
        ILogger<TradovateAccountEventStream> logger)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);

        _webSocket = webSocket;
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public VenueId Venue { get; } = VenueId.Parse("tradovate");

    /// <inheritdoc />
    /// <exception cref="ArgumentException">An account belongs to another venue.</exception>
    /// <exception cref="TradovateVenueException">The trading socket is not connected, or it dropped mid-stream.</exception>
    public IAsyncEnumerable<AccountEvent> StreamAsync(
        IReadOnlyCollection<VenueAccountId> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        // Eager, at the call rather than on the first read (the ProjectX and quote-stream shape): account handles are
        // bare integers that collide freely across venues, so a projectx:9001 arriving here must never be subscribed
        // as TRADOVATE account 9001 -- and discovering that mid-stream would mean events already went the wrong way.
        HashSet<long> subscribed = [.. accounts.Select(account => TradovateMapping.ToAccountId(account, Venue))];

        return ReadAsync(subscribed, cancellationToken);
    }

    private async IAsyncEnumerable<AccountEvent> ReadAsync(
        HashSet<long> subscribed, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The connection host owns the socket. Failing here rather than connecting is what keeps this seam off the
        // manual connect path, which would leave the socket authorized and silent (gh#1048).
        if (_webSocket.TradingState != ClientModels.ConnectionState.Connected)
        {
            throw new TradovateVenueException(
                "The Tradovate trading socket is not connected; account events require the connection host to have "
                + "connected and synced it first (gh#977). An account-event stream never manages the shared socket's "
                + "lifecycle.");
        }

        // WHICH connection this stream is riding, captured before anything else. Only the generation can show that
        // the socket under this stream is no longer the one it checked: a drop AND a reconnect both landing in the
        // window below leave TradingState back at Connected, so the state re-read alone sees nothing (gh#1051).
        long connection = _sync.Generation;

        // Whether the socket had been synced when this stream opened. NOT a refusal -- see the remarks: refusing
        // here would put every stream on the far side of the snapshot and make OnSync unreachable. It is recorded so
        // the stream can say, when it ends, whether it ever rode a socket that was delivering at all.
        bool openedUnsynced = !_sync.IsSynced;

        // Did this stream ever see the socket DELIVER? Set by the snapshot and by every live props frame alike,
        // because either one proves the same thing. Watching only the snapshot reported silence about streams that
        // were not silent at all: a SyncCompleted landing between the sample above and the attach below clears the
        // register while this stream's own handler does not yet exist, and a stream can take live frames with no
        // further SyncCompleted at all -- the client's own reconnect syncs while one of the host's syncs is in
        // flight, so the completion is left to the connection-bound clear and the obligation stays armed even
        // though the socket really is synced (gh#1051 round-2 review).
        bool sawDelivery = false;

        Channel<AccountEvent> events = Channel.CreateUnbounded<AccountEvent>(new UnboundedChannelOptions
        {
            // Never drops. Unlike a quote -- a replaceable snapshot the quote stream sheds under back-pressure -- an
            // account event is truth we cannot lose: a dropped fill or rejection strands an order's status. Account
            // event volume is low, so an unbounded buffer is safe.
            SingleReader = true,
        });

        // One lock over the map, the held fills and the channel writes together. The client dispatches from its
        // receive loop and the connection host raises SyncCompleted from its own, so two threads can genuinely race
        // here -- and an interleaved "resolve the account" and "emit the fill" is how a fill lands under the wrong
        // one. TryWrite on an unbounded channel neither blocks nor runs the reader's continuation inline
        // (AllowSynchronousContinuations stays false), so holding the lock across the write cannot stall the socket.
        object gate = new();

        // Bounded by the orders in one socket session, not by time: entries are two longs, a heavy trading day is
        // hundreds of orders, and the stream ends (taking the map with it) on every socket drop. Deliberately NOT
        // evicted -- an evicted entry would make a late fill unattributable, which is the one thing this map exists
        // to prevent.
        Dictionary<long, long> accountByOrder = [];
        LinkedList<ClientModels.Fill> heldFills = [];
        bool socketDropped = false;

        // Distinct from socketDropped, and not folded into it, because the two end the stream for different reasons
        // and the caller is told which. A socket that was rebuilt underneath this stream never left the connected
        // state as far as this stream could see, so reporting it as a drop would name the wrong cause -- and on a
        // path whose whole justification is that the message IS the trace, that is a correctness defect rather than
        // a wording one.
        bool connectionReplaced = false;

        void Emit(Func<AccountEvent> map, string what)
        {
            // Before the write, not after it: an event this stream could not deliver is still proof the socket was
            // delivering, which is the only thing this flag claims. Callers hold the lock.
            sawDelivery = true;

            try
            {
                if (!events.Writer.TryWrite(map()))
                {
                    // The channel is unbounded, so this is never back-pressure -- it means the stream has already
                    // ended (the drop path completed it) and this event will not be delivered. Rare, and the caller
                    // re-subscribes, but a silent discard here would be exactly the failure this seam guards.
                    _logger.LogWarning(
                        "A Tradovate {What} arrived after the account-event stream had ended; it was not delivered. "
                        + "The re-subscribed stream picks up from the venue's next snapshot.",
                        what);
                }
            }
            catch (Exception error)
            {
                // A payload this adapter refuses -- an open position with no net price, an entity with no id -- costs
                // ONE event, never the feed. A stream that died on a malformed frame would be the silence this seam
                // exists to prevent, and the refusal itself is deliberate (a fabricated price would reach the R-5
                // gate). Loud, because nothing else records it.
                _logger.LogError(error, "A Tradovate {What} could not be mapped onto an account event; skipped.", what);
            }
        }

        // Releases every held fill whose order is now known. A fill that resolves to an account this process did not
        // subscribe is DISCARDED rather than emitted -- it belongs to another account under the same login.
        void ReleaseHeldFills()
        {
            LinkedListNode<ClientModels.Fill>? node = heldFills.First;
            while (node is not null)
            {
                LinkedListNode<ClientModels.Fill>? next = node.Next;
                if (accountByOrder.TryGetValue(node.Value.OrderId, out long accountId))
                {
                    heldFills.Remove(node);
                    EmitFill(node.Value, accountId);
                }

                node = next;
            }
        }

        void EmitFill(ClientModels.Fill fill, long accountId)
        {
            if (!subscribed.Contains(accountId))
            {
                return;
            }

            VenueAccountId account =
                VenueAccountId.Create(Venue, accountId.ToString(CultureInfo.InvariantCulture));
            Emit(() => TradovateMapping.ToFillEvent(fill, account, Venue), "fill");
        }

        void OnOrder(object? sender, ClientModels.Order order)
        {
            lock (gate)
            {
                if (order.Id is { } id)
                {
                    // Recorded for EVERY account, subscribed or not: an order for a foreign account is what lets a
                    // fill held against it be resolved and discarded instead of waiting forever.
                    accountByOrder[id] = order.AccountId;
                }

                if (subscribed.Contains(order.AccountId))
                {
                    Emit(() => TradovateMapping.ToOrderStateEvent(order, Venue), "order");
                }

                ReleaseHeldFills();
            }
        }

        // Emit it if its order is known, hold it if not. Shared by the live props frames and the sync snapshot, so a
        // fill is attributed by one rule whichever way it arrived. Callers hold the lock.
        void AttributeOrHold(ClientModels.Fill fill)
        {
            if (accountByOrder.TryGetValue(fill.OrderId, out long accountId))
            {
                EmitFill(fill, accountId);
                return;
            }

            // Held, never guessed. Attributing a fill to the wrong account would journal a real execution against
            // somebody else's balance, which is worse than the delay -- and worse than losing it.
            //
            // The buffer is shared across every account under this login, so a foreign account's lagging order frames
            // can push ours toward the cap. That is accepted: the alternative is a per-account buffer whose bound
            // nothing could size, and the eviction below is loud precisely because it is the case that loses truth.
            if (heldFills.Count >= MaxHeldFills)
            {
                // Take the NODE, remove THAT node, and log from ITS value. Reading `First` and calling
                // `RemoveFirst()` as two separate statements leaves nothing binding them: change which end goes
                // and the log still names the old end, so the one trace that an execution was discarded would
                // name a fill still sitting in the buffer while the fill actually lost went unnamed. On a branch
                // whose entire justification is that the log IS the trace, that is a correctness hazard, not a
                // coverage gap. Bound this way, any change to which node is removed also changes what is logged,
                // so the whole class of mutation stops being expressible rather than merely being tested for.
                LinkedListNode<ClientModels.Fill> oldest = heldFills.First!;
                heldFills.Remove(oldest);
                _logger.LogError(
                    "Holding {Held} Tradovate fills whose order has never arrived; dropping the oldest (fill "
                    + "{Fill} on order {Order}) to bound the buffer. That execution is lost to the journal, so "
                    + "the day's realized P&L will under-report until it is reconciled from the venue.",
                    heldFills.Count + 1, oldest.Value.Id, oldest.Value.OrderId);
            }

            heldFills.AddLast(fill);
        }

        void OnFill(object? sender, ClientModels.Fill fill)
        {
            lock (gate)
            {
                AttributeOrHold(fill);
            }
        }

        void OnPosition(object? sender, ClientModels.Position position)
        {
            lock (gate)
            {
                if (subscribed.Contains(position.AccountId))
                {
                    Emit(() => TradovateMapping.ToPositionEvent(position, Venue), "position");
                }
            }
        }

        // The snapshot seeds attribution, and emits its FILLS but not its orders or positions -- see the remarks for
        // why those three are treated differently. Orders are the only source of the account behind an order this
        // process never saw a props frame for, which is every order that predates the connect.
        void OnSync(object? sender, ClientModels.SyncResult snapshot)
        {
            lock (gate)
            {
                // This stream has now seen the socket deliver, which is what separates "the account was quiet" from
                // "this socket was never subscribed to anything" when the stream ends (gh#1051).
                sawDelivery = true;

                foreach (ClientModels.Order order in snapshot.Orders)
                {
                    if (order.Id is { } id)
                    {
                        accountByOrder[id] = order.AccountId;
                    }
                }

                // Anything held from before this snapshot first -- its orders may be what names them.
                ReleaseHeldFills();

                // Then the snapshot's own fills, by the same attribution rule: an execution that landed while the
                // socket was down exists NOWHERE else, and one this snapshot cannot name is held rather than guessed.
                //
                // These two sources are NOT disjoint, and the release above does not make them so. Tradovate can
                // push a `props` fill frame from the moment it processes `user/syncrequest` -- before the client
                // raises SyncCompleted, while the map is still empty -- so that fill is held, released here, and
                // then delivered a second time from the snapshot. Emitting it twice is the deliberate cost of the
                // idempotency this whole carve-out rests on: `ProcessFillAsync` dedupes on the unique
                // { OrderId, VenueFillKey } index and `ProcessFlatAsync` on the unique ClosingFillId, so the
                // duplicate costs one redundant round trip, never a double-counted execution. Suppressing it would
                // need this stream to remember every fill it has emitted -- unbounded state to avoid a cost the
                // layer below already absorbs.
                foreach (ClientModels.Fill fill in snapshot.Fills)
                {
                    AttributeOrHold(fill);
                }
            }
        }

        void OnConnectionStatusChanged(object? sender, ClientModels.ConnectionStatusChange change)
        {
            // The TRADING socket only, and only a transition OFF Connected. A transition INTO Connected is the
            // connection host re-arming its sync, and the market-data socket carries no entity frames at all.
            if (!change.IsTradingSocket || change.Current == ClientModels.ConnectionState.Connected)
            {
                return;
            }

            lock (gate)
            {
                socketDropped = true;

                // Completed WITHOUT an error, and the drop is reported by throwing after the drain instead.
                //
                // NOT because completing with the error would lose the buffered events or change the exception type:
                // it would do neither. `WaitToReadAsync` checks the queue BEFORE it checks the completion, so buffered
                // items are still delivered either way, and the error a writer completes with is rethrown as itself,
                // not wrapped. Both shapes end the stream with this same exception -- verified, because a mutant that
                // swapped them survived the first pass of this file's suite.
                //
                // The reason is what the message can say. How many fills this stream was still holding -- executions
                // it will now never attribute to an account -- is only known once the drain is over, and it is the one
                // number that tells the operator how much truth ended with the socket. An exception built here, in a
                // handler that runs before the drain, could not carry it.
                events.Writer.TryComplete();
            }
        }

        // Attached before the first read, so an event arriving between here and the drain loop is not lost. Attach and
        // detach share this iterator's lifecycle: an abandoned stream cannot leave a handler and a filling channel
        // behind, and every exit -- cancellation, the drop, an unconsumed sequence -- runs the finally.
        _webSocket.OrderReceived += OnOrder;
        _webSocket.FillReceived += OnFill;
        _webSocket.PositionReceived += OnPosition;
        _webSocket.SyncCompleted += OnSync;
        _webSocket.ConnectionStatusChanged += OnConnectionStatusChanged;

        try
        {
            // Re-read the state now the drop handler is attached. The check at the top of this method and the attach
            // above are not one step, and a drop landing between them is raised to NOBODY: the handler that would
            // have seen it did not exist yet, so the channel would never be completed and the read below would park
            // forever on a dead socket. The supervisor has no watchdog -- it reacts only to a session that ends or
            // throws -- so nothing above would ever notice. That is the "open sequence over a dead socket" this class
            // exists to prevent, arriving through the one window the event subscription cannot cover.
            //
            // The CONNECTION is re-read for the same reason, and it closes a window the state check alone cannot:
            // a drop AND a reconnect both landing in that gap leave TradingState back at Connected, so the state
            // says "healthy" while the socket underneath is a different connection whose drop was raised to nobody.
            // Only the generation shows it. Deliberately NOT `!IsSynced`: the new connection is legitimately
            // unsynced for a moment, and refusing on that would refuse the ordinary case too (gh#1051).
            //
            // ONE INTERLEAVING FIRES THIS ON A CONNECTION'S FIRST APPEARANCE, and it is accepted rather than
            // hidden. The client writes `_state = Connected` and only then raises the transition that reaches the
            // connection host and bumps the generation, so a stream reading the state inside that gap sees
            // `Connected` against the OLD generation and reports this connection as replaced when it is merely new.
            // Nothing above can distinguish the two from here: both end with the socket Connected on a generation
            // this stream did not capture. The failure direction is the safe one -- the sequence ENDS and the
            // supervisor re-subscribes, rather than continuing silently -- and the residual cost is that the
            // re-subscribed stream may attach after that connection's snapshot. Narrowing it needs the generation
            // to move with the client's own state write rather than after it, which is an upstream change; it is
            // recorded on gh#1051 rather than papered over here.
            if (_webSocket.TradingState != ClientModels.ConnectionState.Connected)
            {
                lock (gate)
                {
                    socketDropped = true;
                    events.Writer.TryComplete();
                }
            }
            else if (_sync.Generation != connection)
            {
                lock (gate)
                {
                    connectionReplaced = true;
                    events.Writer.TryComplete();
                }
            }

            await foreach (AccountEvent accountEvent in events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return accountEvent;
            }

            bool dropped;
            bool replaced;
            int stillHeld;
            lock (gate)
            {
                dropped = socketDropped;
                replaced = connectionReplaced;
                stillHeld = heldFills.Count;
            }

            if (dropped)
            {
                throw new TradovateVenueException(
                    $"The Tradovate trading socket left the connected state with {stillHeld} fill(s) still awaiting "
                    + "the order that names their account; the account-event stream ended so it is re-subscribed "
                    + "rather than left open over a dead socket delivering nothing.");
            }

            if (replaced)
            {
                throw new TradovateVenueException(
                    $"The Tradovate trading socket was replaced by a new connection while this stream was opening, "
                    + $"with {stillHeld} fill(s) still awaiting the order that names their account; the drop was "
                    + "raised before this stream's handler existed, so it ended rather than sitting open over a "
                    + "connection it never subscribed to (gh#1051).");
            }
        }
        finally
        {
            _webSocket.OrderReceived -= OnOrder;
            _webSocket.FillReceived -= OnFill;
            _webSocket.PositionReceived -= OnPosition;
            _webSocket.SyncCompleted -= OnSync;
            _webSocket.ConnectionStatusChanged -= OnConnectionStatusChanged;

            int abandoned;
            bool neverDelivered;
            lock (gate)
            {
                abandoned = heldFills.Count;
                // All three conjuncts earn their place. It opened over a socket nothing had synced; it never saw
                // that socket deliver anything; and the register STILL says unsynced, which rules out a snapshot
                // that landed in the gap between the sample and the attach over a genuinely quiet account.
                neverDelivered = openedUnsynced && !sawDelivery && !_sync.IsSynced;
            }

            if (neverDelivered)
            {
                // The fact gh#1051 exists to make readable, reported at the one moment it is unambiguous. This
                // stream opened over a socket that had never been synced and no snapshot landed while it was open,
                // so Tradovate pushed it no props frame at all: its silence is NOT evidence of a quiet account, and
                // a caller that treated it as one would be reading a position the platform cannot see. The stream
                // is NOT refused for this -- refusing at the top would put every stream on the far side of the
                // snapshot and make the OnSync handler above unreachable, losing the outage fills it exists to
                // recover. The connection host escalates the same condition to the operator.
                _logger.LogWarning(
                    "The Tradovate account-event stream ended having delivered nothing at all over a socket that "
                    + "was unsynced when it opened and is unsynced still. Tradovate delivers entity frames only to "
                    + "a synced socket, so this stream's silence is not evidence about the account.");
            }

            if (abandoned > 0)
            {
                // Real executions this stream could never name. They are not lost quietly: the journal will
                // under-report until they are reconciled from the venue, and this is the only trace of that.
                _logger.LogWarning(
                    "The Tradovate account-event stream ended holding {Held} fill(s) whose order never arrived, so "
                    + "they were never attributed to an account. The day's realized P&L under-reports by that much "
                    + "until it is reconciled from the venue.",
                    abandoned);
            }
        }
    }
}
