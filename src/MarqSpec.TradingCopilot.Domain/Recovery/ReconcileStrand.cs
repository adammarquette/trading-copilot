namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// The identity of one stranded pre-transmit intent (gh#722): its <see cref="Kind"/>, its owning operator (R-20,
/// carried never dropped — the discovery reads across owners), and the offending entity's id. The stable key the
/// in-memory first-seen register and the pure <see cref="ReconcileStrandDetection"/> decision are keyed on.
/// </summary>
/// <param name="Kind">Whether the strand is a mid-take order or a mid-fire conditional.</param>
/// <param name="Owner">The owning operator of the stranded entity (R-20).</param>
/// <param name="EntityId">The stranded order's or conditional's id — the one the operator reconciles.</param>
public readonly record struct ReconcileStrandKey(ReconcileStrandKind Kind, Guid Owner, Guid EntityId);

/// <summary>
/// What the register remembers about a strand across sweep passes (gh#722): when it was <b>first seen</b> stranded
/// (so its age is measured from process observation, not a database timestamp) and whether it has <b>already been
/// alerted</b> (so a continuously-stranded intent alerts exactly once).
/// </summary>
/// <param name="FirstSeen">The instant the sweep first observed this strand — preserved while it stays stranded.</param>
/// <param name="Alerted">Whether the operator alert has already fired for this strand.</param>
public readonly record struct ReconcileStrandEntry(DateTimeOffset FirstSeen, bool Alerted);

/// <summary>
/// The pure result of one reconcile-strand pass (gh#722): which strands are <b>newly alertable</b> this pass, and
/// the <b>next register state</b> the caller should persist (new first-seens added, alerted flags carried forward,
/// resolved ids dropped). Deterministic and side-effect-free — the host raises the alerts and swaps the register.
/// </summary>
/// <param name="NewlyAlertable">
/// The strands that aged past the bound this pass and had not been alerted before — each alerts exactly once.
/// </param>
/// <param name="Register">
/// The register state to keep: one entry per currently-stranded key. A key absent here (present before) is resolved,
/// so a later reappearance is treated fresh (a new first-seen, re-aged before it can alert again).
/// </param>
public sealed record ReconcileStrandDecision(
    IReadOnlyList<ReconcileStrandKey> NewlyAlertable,
    IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> Register);
