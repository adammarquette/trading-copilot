using System.Collections.Concurrent;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The process-wide register of live Tradovate quote subscriptions (R-17, gh#977), and the <b>single serializer</b>
/// of quote subscribe / unsubscribe traffic on the shared market-data socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it remembers.</b> The client replays its own subscriptions when <i>it</i> reconnects — but the only way
/// back from <c>Disconnected</c> (where the client parks a socket after one failed attempt) is the manual connect,
/// and that path deliberately does <b>not</b> replay. Nothing else in the process knows what was subscribed, so
/// without this register a socket the connection host recovers comes back <i>connected but silent</i>: every open
/// <c>StreamQuotesAsync</c> sequence stays alive and simply never ticks again. That is a safety failure with no
/// exception behind it — a stalled quote feed is what stops a hidden stop being promoted.
/// </para>
/// <para>
/// <b>Why it counts.</b> Tradovate allows one subscription per type per contract, so two consumers streaming the
/// same contract share one wire subscription; the first to finish must not unsubscribe it out from under the second.
/// The wire subscribe therefore happens on the <i>first</i> holder and the wire unsubscribe on the <i>last</i>.
/// </para>
/// <para>
/// <b>Why the wire call lives in here rather than at the call site.</b> Deciding "am I the last holder?" and then
/// sending the unsubscribe are two steps with a network round trip between them. If a second consumer claimed the
/// same contract in that window and subscribed, the first one's late unsubscribe would kill the newcomer's feed —
/// and neither recovery path would notice: the client removes its own record before sending, so its reconnect will
/// not replay the key, and the socket never leaves <c>Connected</c>, so the host never
/// replays either. One silent feed, permanently. Every transition therefore runs under this type's gate, so a
/// count change and the wire call it implies cannot interleave with another.
/// </para>
/// </remarks>
public sealed class TradovateQuoteSubscriptions
{
    // Serializes each (count transition + the wire call it implies) as one unit. An async gate rather than a lock
    // because the wire call is awaited; not disposed, because a SemaphoreSlim only holds a disposable resource once
    // AvailableWaitHandle is read, and this one is a process-lifetime singleton.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Mutated only under the gate; concurrent for the sake of LiveKeys, which is read outside it by the connection
    // host's poll.
    private readonly ConcurrentDictionary<string, int> _holders = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a snapshot of the keys with at least one holder, in no particular order.
    /// </summary>
    /// <remarks>
    /// A <b>copy</b>, not a view: the connection host walks this list while awaiting a subscribe per key, and a live
    /// view would let a stream starting or ending mid-walk disturb the replay.
    /// </remarks>
    public IReadOnlyList<string> LiveKeys => [.. _holders.Keys];

    /// <summary>
    /// Claims a holder on <paramref name="subscriptionKey"/> and, when it is the <b>first</b>, subscribes the wire —
    /// both under one gate, so no concurrent release can unsubscribe between the two.
    /// </summary>
    /// <param name="subscriptionKey">
    /// The key the wire subscription is made with — the same string the client's subscribe takes, so a replay
    /// reproduces it exactly.
    /// </param>
    /// <param name="subscribe">Sends the wire subscribe. Called only for the first holder.</param>
    /// <param name="cancellationToken">A token to cancel the wait and the subscribe.</param>
    /// <remarks>
    /// The claim is registered <b>before</b> the wire subscribe is awaited, so a reconnect the connection host drives
    /// in between still replays this contract. If the subscribe throws, the claim is rolled back and the exception
    /// propagates — the caller never holds a claim it did not get.
    /// </remarks>
    public async Task AcquireAsync(
        string subscriptionKey,
        Func<CancellationToken, Task> subscribe,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionKey);
        ArgumentNullException.ThrowIfNull(subscribe);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_holders.AddOrUpdate(subscriptionKey, 1, (_, holders) => holders + 1) > 1)
            {
                return; // another holder already has this contract on the wire
            }

            try
            {
                await subscribe(cancellationToken);
            }
            catch
            {
                Drop(subscriptionKey);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives up a holder's claim on <paramref name="subscriptionKey"/> and, when it was the <b>last</b>,
    /// unsubscribes the wire — both under the same gate.
    /// </summary>
    /// <param name="subscriptionKey">The key passed to <see cref="AcquireAsync"/>.</param>
    /// <param name="unsubscribe">Sends the wire unsubscribe. Called only for the last holder.</param>
    /// <returns>
    /// <see langword="true"/> when the caller was the last holder, so the wire unsubscribe was attempted;
    /// <see langword="false"/> when another holder remains, and for a key that was never acquired — an unbalanced
    /// release must not drive the count below zero, or a later real release would report "not the last holder" and
    /// leave the wire subscribed forever.
    /// </returns>
    public async Task<bool> ReleaseAsync(string subscriptionKey, Func<Task> unsubscribe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionKey);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        // Never cancellable: this runs on a stream's teardown path, and abandoning it half-done would leak the
        // holder count that decides whether anyone may ever unsubscribe the contract again.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!Drop(subscriptionKey))
            {
                return false;
            }

            await unsubscribe();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-sends the wire subscribe for one key after a reconnect, under the same gate — but only while the key is
    /// still held.
    /// </summary>
    /// <param name="subscriptionKey">The key to replay.</param>
    /// <param name="subscribe">Sends the wire subscribe.</param>
    /// <param name="cancellationToken">A token to cancel the wait and the subscribe.</param>
    /// <returns>
    /// <see langword="true"/> when the subscribe was sent; <see langword="false"/> when the key is no longer held —
    /// its stream ended while the socket was down, and replaying it would feed a channel with no reader for the rest
    /// of the session.
    /// </returns>
    public async Task<bool> ResubscribeAsync(
        string subscriptionKey,
        Func<CancellationToken, Task> subscribe,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionKey);
        ArgumentNullException.ThrowIfNull(subscribe);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_holders.ContainsKey(subscriptionKey))
            {
                return false;
            }

            await subscribe(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called only under the gate, so the read-then-write is safe. True when the LAST holder left.
    private bool Drop(string subscriptionKey)
    {
        if (!_holders.TryGetValue(subscriptionKey, out int holders))
        {
            return false;
        }

        if (holders > 1)
        {
            _holders[subscriptionKey] = holders - 1;
            return false;
        }

        _holders.TryRemove(subscriptionKey, out _);
        return true;
    }
}
