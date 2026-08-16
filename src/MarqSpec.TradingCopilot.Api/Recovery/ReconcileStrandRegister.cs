using System.Collections.Concurrent;
using MarqSpec.TradingCopilot.Domain.Recovery;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The in-memory first-seen register behind the runtime reconcile-strand sweep (gh#722). It remembers, <b>per
/// process lifetime</b>, when the sweep first saw each strand (an order left <c>Taking</c> / a conditional left
/// <c>Firing</c>) and whether it has already been alerted — so age is measured from observation and a
/// continuously-stranded intent alerts exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-memory by design</b> — the age of a <i>runtime</i> strand is measured from when the sweep saw it become
/// stranded, not from a database column (there is deliberately no timestamp column, ADR-0013). A strand that
/// survives a <b>restart</b> is not this sweep's concern: the <see cref="DecisionStateRehydrator"/> flags it at boot
/// (fail-safe + loud), so the register starting empty each boot is correct, not a gap.
/// </para>
/// <para>
/// <b>Thread-safe over a <see cref="ConcurrentDictionary{TKey,TValue}"/></b> — defensive, since the single sweep
/// loop is the only writer (the <see cref="Accounts.PendingFlatJournal"/> discipline). A singleton, so it survives
/// across the sweep's per-pass DI scopes; it does <b>not</b> survive a process restart. The reasoning — first-seen
/// preserved, resolved dropped, alerted-once — lives in the pure <see cref="ReconcileStrandDetection"/>; this type
/// only holds the state and swaps it under <see cref="Reconcile"/>.
/// </para>
/// </remarks>
public sealed class ReconcileStrandRegister
{
    private readonly ConcurrentDictionary<ReconcileStrandKey, ReconcileStrandEntry> _entries = new();

    /// <summary>
    /// A snapshot of the register's current memory — a stable copy the pure decision reads against, so a concurrent
    /// mutation cannot shift the map mid-pass.
    /// </summary>
    /// <returns>The first-seen instant and alerted flag per strand currently remembered.</returns>
    public IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> Snapshot() =>
        new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>(_entries);

    /// <summary>
    /// Swaps the register to the pure decision's <paramref name="next"/> state: strands absent from it (resolved
    /// since last pass) are dropped, and every surviving / newly-seen strand is written with its preserved first-seen
    /// and carried-forward alerted flag.
    /// </summary>
    /// <param name="next">The register state <see cref="ReconcileStrandDetection.Detect"/> computed for this pass.</param>
    public void Reconcile(IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Drop resolved strands first (remembered now, absent from next) so a later reappearance is re-seen fresh.
        foreach (ReconcileStrandKey resolved in _entries.Keys.Where(key => !next.ContainsKey(key)).ToList())
        {
            _entries.TryRemove(resolved, out _);
        }

        // Then upsert the surviving and first-seen entries with their (decision-computed) first-seen and alerted flag.
        foreach (KeyValuePair<ReconcileStrandKey, ReconcileStrandEntry> entry in next)
        {
            _entries[entry.Key] = entry.Value;
        }
    }
}
