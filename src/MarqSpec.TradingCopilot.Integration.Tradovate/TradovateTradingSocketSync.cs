namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>What the Tradovate trading socket still owes before it delivers entity events at all (gh#1051).</summary>
/// <remarks>
/// Ordered by how certain the connection host is that it must act, because the two "connected" cases are not the
/// same obligation.
/// </remarks>
public enum TradovateSyncObligation
{
    /// <summary>A snapshot has landed since the socket last connected — the feed is live.</summary>
    None = 0,

    /// <summary>
    /// The socket reported <c>Connected</c> and the host did not drive it, so the client's own reconnect may be about
    /// to sync it. One pass of patience, then <see cref="Due"/>.
    /// </summary>
    Pending = 1,

    /// <summary>Nothing else will sync this connection. Send it.</summary>
    Due = 2,
}

/// <summary>
/// The process-wide record of whether the Tradovate <b>trading</b> socket has actually been synced (R-17, gh#977,
/// gh#1051) — written by <c>TradovateTradingConnectionHost</c>, read by anything that would otherwise mistake a
/// socket that was never subscribed for a quiet account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fact has to be readable.</b> Tradovate pushes <c>props</c> entity frames — the sole source of order,
/// position, fill and cash-balance events — only to a socket that has completed <c>user/syncrequest</c>. A socket
/// that is open and authorized but unsynced is therefore <i>connected and permanently silent</i>, with no exception
/// anywhere, and <c>ITradovateWebSocketClient.TradingState</c> reports <c>Connected</c> throughout. Every consumer
/// above it — the account-event stream above all — sees exactly what a quiet account looks like. Since gh#1069
/// carries real fills into the ledger, that silence means the ledger disagrees with the world: no <c>Fill</c> row, no
/// composed <c>Trade</c>, and a realized loss that never reaches the R-5 governor, the R-9 window or the R-4
/// throttle, which then read headroom that does not exist.
/// </para>
/// <para>
/// <b>One home, not two.</b> The connection host used to hold this as a private field, which was correct for the host
/// and invisible to everyone else. A second copy next to it would be a guard that drifts — the failure this codebase
/// has already paid for twice (gh#1054) — so the host drives this type instead of shadowing it, and the state the
/// stream reads is the same state the host acts on, by construction.
/// </para>
/// <para>
/// <b>The generation, and the race it closes.</b> <c>ConnectUnlockedAsync</c> → <c>FailPending</c> faults only the
/// requests still sitting in the client's pending map; a response its receive loop has <i>already</i> dispatched
/// resolves whatever happens to the transport afterwards, and the sync then raises <c>SyncCompleted</c> regardless.
/// So a sync started on one connection can complete after a fresh connect has re-armed the obligation, and clearing
/// it there would leave the <b>new</b> connection permanently unsynced. Every host-driven clear is therefore bound to
/// the connection the sync was started on: <see cref="BeginHostSync"/> hands out that binding and
/// <see cref="CompleteHostSync"/> refuses without it.
/// </para>
/// <para>
/// <b>Why a completion the host did not send is taken at face value.</b> The client raises it from inside
/// <c>ReconnectAsync</c>, which holds <c>_connectGate</c> across both <c>SetState(Connected)</c> and the sync that
/// follows — and every other transition into <c>Connected</c>, manual connect included, must take that same gate. So
/// no newer connection can interleave between a client-driven sync and its completion, and
/// <see cref="CompleteObservedSync"/> needs no generation. That is read from the client's source rather than assumed;
/// it is the reason the two kinds of completion are distinguished at all.
/// </para>
/// <para>
/// <b>The direction every ambiguity errs in.</b> When the two cannot be told apart — the host has a sync in flight
/// and a completion arrives — the obligation is <i>kept</i>. The cost is one duplicate snapshot, which consumers
/// dedupe by construction (<c>{ OrderId, VenueFillKey }</c>); the cost of the opposite mistake is a socket nothing
/// ever syncs again. This type never trades silence for tidiness.
/// </para>
/// </remarks>
public sealed class TradovateTradingSocketSync
{
    // Written from the client's event handlers (arbitrary threads) as well as the connection host's poll loop, so
    // every read and write goes through Interlocked/Volatile. Starts at Pending rather than None: a socket already
    // Connected when this process starts was synced by nothing it can see.
    private int _obligation = (int)TradovateSyncObligation.Pending;

    // Bumped on every transition into Connected. Only ever compared for equality, so wrap-around is irrelevant.
    private long _generation;

    // How many syncs the HOST currently has in flight. A counter rather than a flag: the host sends one at a time
    // today, but a flag would silently lose the second one if that ever stopped being true.
    private int _hostSyncsInFlight;

    /// <summary>
    /// Gets a value indicating whether a snapshot has landed for the connection the socket currently holds — the fact
    /// that separates a quiet account from a socket that was never subscribed to anything.
    /// </summary>
    public bool IsSynced => Obligation == TradovateSyncObligation.None;

    /// <summary>Gets what the socket still owes before it delivers entity events.</summary>
    public TradovateSyncObligation Obligation => (TradovateSyncObligation)Volatile.Read(ref _obligation);

    /// <summary>
    /// Records a transition <b>into</b> <c>Connected</c>: a new connection carries no entity subscription, so the
    /// obligation is re-armed with one pass of grace whoever drove the connect.
    /// </summary>
    /// <remarks>
    /// Assigned rather than escalated, and the generation moves with it, so a sync still in flight over the previous
    /// connection can no longer clear what this one owes.
    /// </remarks>
    public void OnSocketConnected()
    {
        // Generation first. A CompleteHostSync racing this either sees the new generation and refuses (safe), or
        // sees the old one and clears -- and the write below then re-arms over it (also safe). Both orders keep the
        // obligation; neither can lose it.
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _obligation, (int)TradovateSyncObligation.Pending);
    }

    /// <summary>
    /// Records that a sync is owed with <b>no grace</b> — the shape a connect the host itself drove leaves, because
    /// the manual connect path authorizes the socket and sends nothing else.
    /// </summary>
    public void RequireSync() => Volatile.Write(ref _obligation, (int)TradovateSyncObligation.Due);

    /// <summary>
    /// Spends the grace pass: the client's own reconnect did not sync this connection after all, so the host must.
    /// </summary>
    /// <returns><see langword="true"/> when the obligation moved from pending to due.</returns>
    public bool PromoteGraceToDue() =>
        Interlocked.CompareExchange(
            ref _obligation, (int)TradovateSyncObligation.Due, (int)TradovateSyncObligation.Pending)
        == (int)TradovateSyncObligation.Pending;

    /// <summary>
    /// Marks the start of a sync the <b>host</b> is sending and returns the connection it belongs to.
    /// </summary>
    /// <returns>The generation to hand back to <see cref="CompleteHostSync"/>.</returns>
    public long BeginHostSync()
    {
        Interlocked.Increment(ref _hostSyncsInFlight);
        return Volatile.Read(ref _generation);
    }

    /// <summary>Marks the end of a host-sent sync, whether it landed or failed.</summary>
    /// <remarks>
    /// Must run in a <c>finally</c>. Leaving the marker set would suppress every later client-driven completion for
    /// the rest of the process's life, so a reconnect the client synced itself would read as unsynced forever.
    /// </remarks>
    public void EndHostSync() => Interlocked.Decrement(ref _hostSyncsInFlight);

    /// <summary>
    /// Clears the obligation for a snapshot the host sent, but <b>only</b> for the connection it was started on and
    /// only when that connection still owed one.
    /// </summary>
    /// <param name="generation">The value <see cref="BeginHostSync"/> returned.</param>
    /// <returns><see langword="true"/> when the obligation was cleared.</returns>
    public bool CompleteHostSync(long generation)
    {
        if (Volatile.Read(ref _generation) != generation)
        {
            return false;
        }

        // From Due only. A Pending obligation is one the host has not taken on -- the grace pass is still waiting for
        // the client's own reconnect -- and clearing it here would cancel a wait on behalf of a sync that answered a
        // different question.
        return Interlocked.CompareExchange(
                   ref _obligation, (int)TradovateSyncObligation.None, (int)TradovateSyncObligation.Due)
               == (int)TradovateSyncObligation.Due;
    }

    /// <summary>
    /// Clears the obligation for a snapshot that arrived without the host asking — the client's own reconnect syncing
    /// itself, which is the whole reason the completion event is watched rather than only what the host sent.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the obligation was cleared; <see langword="false"/> while a host-sent sync is in
    /// flight, because that completion is bound to a connection and this path cannot honour the binding.
    /// </returns>
    public bool CompleteObservedSync()
    {
        if (Volatile.Read(ref _hostSyncsInFlight) != 0)
        {
            return false;
        }

        // Unconditional, and safe for exactly the reason the remarks give: the client holds its connect gate across
        // both the SetState(Connected) and the sync that follows it, so no newer connection can have interleaved.
        // This clears a PENDING obligation, which is what the grace pass exists to wait for.
        Volatile.Write(ref _obligation, (int)TradovateSyncObligation.None);
        return true;
    }
}
