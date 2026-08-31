namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The process-wide register of live Tradovate quote subscriptions (R-17, gh#977): which market-data keys the
/// adapter currently has consumers for, and how many.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The client remembers its own subscriptions and replays them when <i>it</i> reconnects —
/// but the only way back from <c>Disconnected</c> (where the client parks a socket after one failed attempt) is
/// the manual connect, and that path deliberately does <b>not</b> replay. Nothing else in the process knows what
/// was subscribed, so without this register a socket the connection host recovers comes back
/// <i>connected but silent</i>: every open <c>StreamQuotesAsync</c> sequence stays alive and simply never ticks
/// again. That is a safety failure with no exception behind it — a stalled quote feed is what stops a hidden
/// stop being promoted.
/// </para>
/// <para>
/// <b>Why it counts rather than sets.</b> Tradovate allows one subscription per type per contract, so two
/// consumers streaming the same contract share one wire subscription; the first to finish must not unsubscribe it
/// out from under the second. <see cref="Release"/> reports whether the caller was the <i>last</i> holder, which
/// is exactly when the wire unsubscribe is safe.
/// </para>
/// <para>Every member is safe to call concurrently — streams start and stop on their consumers' threads while the
/// connection host enumerates <see cref="LiveKeys"/> on its own.</para>
/// </remarks>
public sealed class TradovateQuoteSubscriptions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _holders = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a snapshot of the keys with at least one holder, in no particular order.
    /// </summary>
    /// <remarks>
    /// A <b>copy</b>, not a view: the connection host walks this list while awaiting a subscribe per key, and a
    /// live view would throw part-way through and leave the socket half-subscribed.
    /// </remarks>
    public IReadOnlyList<string> LiveKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _holders.Keys];
            }
        }
    }

    /// <summary>Records one more holder of <paramref name="subscriptionKey"/>.</summary>
    /// <param name="subscriptionKey">
    /// The key the wire subscription was made with — the same string passed to the client's subscribe, so a replay
    /// reproduces it exactly.
    /// </param>
    public void Track(string subscriptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionKey);

        lock (_gate)
        {
            _holders[subscriptionKey] = _holders.TryGetValue(subscriptionKey, out int holders) ? holders + 1 : 1;
        }
    }

    /// <summary>Gives up one holder's claim on <paramref name="subscriptionKey"/>.</summary>
    /// <param name="subscriptionKey">The key passed to <see cref="Track"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the caller was the last holder and the key has left the register — the only
    /// point at which unsubscribing the shared wire subscription is safe. <see langword="false"/> when another
    /// holder remains, and for a key that was never tracked: an unbalanced release must not drive the count below
    /// zero, or a later real release would report "not the last holder" and leave the wire subscribed forever.
    /// </returns>
    public bool Release(string subscriptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionKey);

        lock (_gate)
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

            _holders.Remove(subscriptionKey);
            return true;
        }
    }
}
