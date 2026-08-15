using System.Collections.Concurrent;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// The in-memory register of flats awaiting their closing fill (gh#748). A <see cref="PositionEvent"/> (flat) and a
/// <see cref="FillEvent"/> (fill) are independent, unordered venue callbacks into one stream: the flat can be
/// processed <b>before</b> the fill that closed the round trip is ingested. A flat whose window does not yet reconcile
/// to flat is parked here and retried by <see cref="AccountEventStreamHost"/> when a fill for that account lands —
/// rather than mislabelled not-composable and lost, which would leave the round trip's realized P&amp;L out of the
/// R-5 / R-9 / R-4 governors permanently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed on <c>(Account, Contract.Key, At)</c>.</b> The flat's own instant is part of the key so <b>two distinct
/// round trips on the same contract</b> can be deferred at once — each awaiting its own closing fill — without the
/// second overwriting the first (the silent second-cycle loss the gh#748 review caught in a single-slot
/// <c>(Account, Contract)</c> key). A reconnect re-delivering the <b>same</b> flat carries the same <c>At</c>, so
/// <see cref="Register"/> still collapses it to one entry (first-seen wins); a genuinely re-timestamped redelivery
/// parks a harmless duplicate — the gh#759 <c>(ClosingFillId, OpeningFillId)</c> unique index rejects the second write.
/// </para>
/// <para>
/// <b>In-memory by design.</b> Thread-safe over a <see cref="ConcurrentDictionary{TKey,TValue}"/> (defensive — the
/// account-event loop is single-threaded). A singleton, so it survives the supervisor's drop-and-reconnect cycles; it
/// does <b>not</b> survive a process restart. The restart-crosses-the-race edge, a closing fill that <b>never</b>
/// arrives, and a clock-skewed flat whose fill falls outside the writer's <c>ExecutedAt &lt;= At</c> window all stay
/// with gh#722's reconcile sweep — a deferral that never resolves to <c>journalled</c> stays observable through the
/// recurring <c>deferred</c> metric in the meantime.
/// </para>
/// </remarks>
public sealed class PendingFlatJournal
{
    private readonly ConcurrentDictionary<(VenueAccountId Account, string Contract, DateTimeOffset At), PositionEvent> _pending = new();

    /// <summary>
    /// Parks <paramref name="exit"/> awaiting its closing fill, keyed on <c>(Account, Contract.Key, At)</c>.
    /// Re-registering the identical flat (a reconnect re-delivery — same <c>At</c>) <b>preserves the first-seen</b>
    /// event; a distinct cycle on the same contract (a different <c>At</c>) parks alongside it, not over it.
    /// </summary>
    /// <param name="exit">The flat position event to defer.</param>
    public void Register(PositionEvent exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        _pending.TryAdd((exit.Account, exit.Contract.Key, exit.At), exit);
    }

    /// <summary>Removes the deferral held for <paramref name="exit"/> (matched on <c>(Account, Contract.Key, At)</c>).</summary>
    /// <param name="exit">The flat whose deferral to clear — the same event that was <see cref="Register"/>ed.</param>
    public void Resolve(PositionEvent exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        _pending.TryRemove((exit.Account, exit.Contract.Key, exit.At), out _);
    }

    /// <summary>
    /// A snapshot of the flats currently deferred for <paramref name="account"/> — read only, nothing is removed.
    /// </summary>
    /// <param name="account">The account to filter by.</param>
    /// <returns>The deferred exits for that account; empty when none are parked.</returns>
    public IReadOnlyCollection<PositionEvent> PendingFor(VenueAccountId account) =>
        [.. _pending.Where(entry => entry.Key.Account == account).Select(entry => entry.Value)];
}
