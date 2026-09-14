namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// The pure heart of the runtime reconcile-strand sweep (gh#722, ADR-0013): given the intents a sweep pass found
/// stranded, the register's memory of past passes, the clock and an age bound, decide which strands are <b>newly
/// alertable</b> and what the register should remember next. Deterministic and side-effect-free — the composition
/// layer feeds it from EF discovery and acts on the verdict (raise the operator alert, swap the register), so the
/// safety logic lives below the model and is exhaustively unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// This <b>detects and alerts, never acts</b>: it proposes that the operator reconcile a strand (via the existing
/// <c>POST /orders/{id}/reconcile</c> / <c>POST /conditionals/{id}/reconcile</c>), transmitting nothing, adopting
/// nothing, releasing nothing and changing no order state — preserving the "never auto-act on rehydrated state"
/// invariant (ADR-0013). It is the <b>runtime</b> counterpart of <see cref="DecisionStateRehydration"/>, which
/// covers the restart case: a strand that survives a restart is flagged by the rehydrator (fail-safe + loud), so
/// this register starting empty at boot is not a gap.
/// </para>
/// <para>
/// <b>Age is measured from first observation</b>, not a database timestamp: a runtime strand is seen becoming
/// <c>Taking</c> / <c>Firing</c> by the sweep and clocked from then. <b>Idempotent</b> — a strand alerts exactly
/// once while continuously stranded, so a two-minute strand does not re-page every pass. <b>Fresh on
/// reappearance</b> — a resolved strand is dropped, so an id that returns is re-aged before it can alert again
/// (it is a different episode, and a genuinely re-stranded intent deserves a fresh look).
/// </para>
/// </remarks>
public static class ReconcileStrandDetection
{
    /// <summary>
    /// Decides one reconcile-strand pass: which strands newly cross the age bound (and were not already alerted),
    /// and the register state to keep.
    /// </summary>
    /// <param name="stranded">Every strand the pass found (deduplicated defensively).</param>
    /// <param name="register">The register's current memory: first-seen instant and alerted flag per strand.</param>
    /// <param name="now">The pass instant — the clock is read at the boundary and passed in (the decision stays pure).</param>
    /// <param name="ageBound">How long a strand must persist before it is alertable.</param>
    /// <returns>The newly-alertable strands and the next register state.</returns>
    public static ReconcileStrandDecision Detect(
        IReadOnlyList<ReconcileStrandKey> stranded,
        IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> register,
        DateTimeOffset now,
        TimeSpan ageBound)
    {
        ArgumentNullException.ThrowIfNull(stranded);
        ArgumentNullException.ThrowIfNull(register);

        // Dedupe defensively: the same id must never be double-counted, whatever the discovery hands us.
        HashSet<ReconcileStrandKey> current = [.. stranded];
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> next = new(current.Count);
        List<ReconcileStrandKey> newlyAlertable = [];

        foreach (ReconcileStrandKey key in current)
        {
            // First-seen is preserved across passes; a strand the register has never seen is first seen NOW. So a
            // brand-new strand has age zero and cannot alert on the pass it first appears -- only once it has
            // persisted at least the bound (config validation forbids a non-positive bound, so age-zero never alerts).
            bool known = register.TryGetValue(key, out ReconcileStrandEntry existing);
            DateTimeOffset firstSeen = known ? existing.FirstSeen : now;
            bool alreadyAlerted = known && existing.Alerted;

            bool aged = now - firstSeen >= ageBound;
            bool alertNow = aged && !alreadyAlerted;
            if (alertNow)
            {
                newlyAlertable.Add(key);
            }

            // Carry the alerted flag forward once set, so a still-stranded intent never re-alerts (the idempotency
            // this sweep exists to guarantee -- a maybe-live strand pages the operator once, not on every pass).
            next[key] = new ReconcileStrandEntry(firstSeen, alreadyAlerted || alertNow);
        }

        // Any key the register held that is no longer stranded is RESOLVED -- it simply does not appear in `next`, so
        // the caller drops it. A later reappearance is then unknown again: re-seen, re-aged, and alertable afresh.
        return new ReconcileStrandDecision(newlyAlertable, next);
    }
}
