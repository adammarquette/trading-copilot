namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// An <b>impossible decision-state combination</b> a crash mid-write can leave (gh#221, ADR-0013 §9). These are
/// <b>cross-entity</b> invariants — beyond what a single-row DB check can express — that must hold once every
/// individual row is valid: a crash between two separate writes leaves each row legal but the whole surface
/// contradictory. The rehydration pass detects them and fails <b>safe and loud</b>; it never silently repairs.
/// </summary>
public enum DecisionInconsistencyKind
{
    /// <summary>Not a finding — the refusable zero (the fail-closed-zero convention, gh#60). Never produced.</summary>
    Unknown = 0,

    /// <summary>A <see cref="Execution.OrderStatus.Staged"/> order carries a venue handle — staged means <b>not</b> at the venue.</summary>
    StagedOrderAtVenue = 1,

    /// <summary>A <see cref="Execution.ConditionalStatus.Fired"/> conditional has no linked order id — it fired into nothing.</summary>
    ConditionalFiredWithoutOrder = 2,

    /// <summary>A fired conditional links an order id that no rehydrated order matches — the link dangles.</summary>
    ConditionalFiredOrderMissing = 3,

    /// <summary>A not-yet-fired conditional (pending / cancelled / expired) already links an order — only a fired one may.</summary>
    ConditionalUnfiredWithOrder = 4,

    /// <summary>A stop plan protects an order that no rehydrated order matches — the plan is orphaned from its order.</summary>
    StopPlanWithoutOrder = 5,

    /// <summary>A <see cref="Execution.StopStaging.Native"/> (at-venue) stop protects an order that is not live.</summary>
    NativeStopWithoutLiveOrder = 6,

    /// <summary>A stop plan's owner differs from its order's owner — ownership drifted across the two rows (R-20).</summary>
    StopPlanOwnerMismatch = 7,

    /// <summary>
    /// A <see cref="Execution.ConditionalStatus.Firing"/> conditional persists at rest — a durable pre-transmit
    /// intent (gh#577) that a crash caught mid-flight. Firing is transient: the firing pass moves it on to
    /// <see cref="Execution.ConditionalStatus.Fired"/> or back to <see cref="Execution.ConditionalStatus.Pending"/>
    /// within one fire. Found surviving a restart, its order <b>may be live at the venue</b> while the journal never
    /// recorded it — reconcile against venue truth (by the order's <c>customTag</c>) before any resumption; never
    /// silently repaired.
    /// </summary>
    ConditionalMidFiring = 8,

    /// <summary>
    /// A <see cref="Execution.OrderStatus.Taking"/> order persists at rest — the take path's analog of
    /// <see cref="ConditionalMidFiring"/> (gh#589). The claim (<see cref="Execution.OrderStatus.Staged"/> →
    /// <see cref="Execution.OrderStatus.Taking"/>, gh#530) is the take's durable pre-transmit intent; taking is
    /// transient at runtime (one request resolves it to <see cref="Execution.OrderStatus.Working"/> or releases it
    /// back to <see cref="Execution.OrderStatus.Staged"/>). Found surviving a restart, its order <b>may be live at
    /// the venue</b> while the journal never marked it working — reconcile against venue truth (by the order's
    /// <c>customTag</c>) before any resumption; never silently repaired.
    /// </summary>
    OrderMidTaking = 9,
}
