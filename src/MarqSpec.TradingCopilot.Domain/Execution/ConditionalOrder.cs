using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// A <b>synthetic / local</b> conditional entry order (ADR-0007, gh#176): the second send mode — "send when
/// conditions are met". The platform holds the proposal and fires it through the gate when the
/// <see cref="Trigger"/> crosses; it is <b>not shown at the broker</b> until then. It self-cancels on an adverse
/// drift past its cancel band, or when its validity window passes, so it cannot fire on a stale setup or rest
/// forever.
/// </summary>
/// <remarks>
/// The decisions are deterministic and <b>Pending-only</b> — the same discipline as <see cref="StopPlan"/>:
/// <see cref="ShouldFire"/> / <see cref="ShouldCancel"/> are what a watcher consults, and <see cref="Fire"/> /
/// <see cref="Cancel"/> / <see cref="Expire"/> are one-way and idempotent so a retrying watcher never re-fires a
/// resolved order. The authoritative R-5 / R-12 / R-16 re-check happens at fire time (the watcher's job); this
/// type carries the proposal and the trigger, not permission to transmit.
/// </remarks>
public sealed record ConditionalOrder
{
    private ConditionalOrder(
        OrderProposal proposal,
        ConditionalTrigger trigger,
        Price? cancelDrift,
        DateTimeOffset? expiresAt,
        ConditionalStatus status)
    {
        Proposal = proposal;
        Trigger = trigger;
        CancelDrift = cancelDrift;
        ExpiresAt = expiresAt;
        Status = status;
    }

    /// <summary>The entry to transmit when the trigger fires — sized and gated afresh at fire time.</summary>
    public OrderProposal Proposal { get; }

    /// <summary>The condition that fires the order.</summary>
    public ConditionalTrigger Trigger { get; }

    /// <summary>
    /// The cancel band, when set: the price on the <b>stale side</b> of the trigger past which the setup is
    /// abandoned (below the trigger for <see cref="ConditionalCrossDirection.RisesTo"/>, above it for
    /// <see cref="ConditionalCrossDirection.FallsTo"/>). <see langword="null"/> means no drift cancel.
    /// </summary>
    public Price? CancelDrift { get; }

    /// <summary>The validity deadline, when set. Past it, a still-pending order cancels.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Where the order is in its lifecycle.</summary>
    public ConditionalStatus Status { get; }

    /// <summary>Creates a <see cref="ConditionalStatus.Pending"/> order, validating every invariant.</summary>
    /// <param name="proposal">The entry to fire.</param>
    /// <param name="trigger">The fire condition; its direction must be declared.</param>
    /// <param name="cancelDrift">The optional cancel band; must sit on the stale side of the trigger.</param>
    /// <param name="expiresAt">The optional validity deadline.</param>
    /// <returns>The pending order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The trigger direction is <see cref="ConditionalCrossDirection.Unknown"/>, or the cancel band sits on the
    /// wrong side of the trigger.
    /// </exception>
    public static ConditionalOrder Create(
        OrderProposal proposal,
        ConditionalTrigger trigger,
        Price? cancelDrift = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger.Direction is not (ConditionalCrossDirection.RisesTo or ConditionalCrossDirection.FallsTo))
        {
            throw new ArgumentOutOfRangeException(
                nameof(trigger),
                trigger.Direction,
                "A conditional order must declare a cross direction (RisesTo or FallsTo); Unknown never fires.");
        }

        // The cancel band, when set, sits on the STALE side of the trigger -- opposite the cross the order is
        // waiting for. A RisesTo order (waiting for price to rise to the trigger) goes stale if price falls away
        // below it, so its band is BELOW the trigger; a FallsTo order's band is ABOVE. A band on the near side
        // would cancel the instant price approached the trigger -- the opposite of what it is for.
        if (cancelDrift is { } drift)
        {
            bool ordered = trigger.Direction switch
            {
                ConditionalCrossDirection.RisesTo => drift.Value < trigger.TriggerPrice.Value,
                ConditionalCrossDirection.FallsTo => drift.Value > trigger.TriggerPrice.Value,
                _ => false,
            };

            if (!ordered)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cancelDrift),
                    drift.Value,
                    $"A {trigger.Direction} order's cancel band must sit on the stale side of the trigger "
                    + $"{trigger.TriggerPrice.Value} — below it for RisesTo, above it for FallsTo.");
            }
        }

        return new ConditionalOrder(proposal, trigger, cancelDrift, expiresAt, ConditionalStatus.Pending);
    }

    /// <summary>Whether the trigger has fired and the order is still waiting. Always false once resolved.</summary>
    /// <param name="current">The current market price.</param>
    /// <returns><see langword="true"/> when the entry should be transmitted now.</returns>
    public bool ShouldFire(Price current)
    {
        return Status == ConditionalStatus.Pending && Trigger.HasFired(current);
    }

    /// <summary>
    /// Whether a still-pending order should be abandoned now — its validity window has passed, or price has
    /// drifted past the cancel band on the stale side. Always false once resolved.
    /// </summary>
    /// <param name="current">The current market price.</param>
    /// <param name="now">The current time, supplied by the caller (never read from the clock here).</param>
    /// <returns><see langword="true"/> when the order should be cancelled.</returns>
    public bool ShouldCancel(Price current, DateTimeOffset now)
    {
        if (Status != ConditionalStatus.Pending)
        {
            return false;
        }

        if (ExpiresAt is { } expiry && now >= expiry)
        {
            return true;
        }

        return CancelDrift is { } drift && Trigger.Direction switch
        {
            ConditionalCrossDirection.RisesTo => current.Value <= drift.Value,
            ConditionalCrossDirection.FallsTo => current.Value >= drift.Value,
            _ => false,
        };
    }

    /// <summary>Marks the order fired. One-way from pending; a no-op once resolved.</summary>
    /// <returns>The fired order.</returns>
    public ConditionalOrder Fire()
    {
        return Transition(ConditionalStatus.Fired);
    }

    /// <summary>Marks the order cancelled. One-way from pending; a no-op once resolved.</summary>
    /// <returns>The cancelled order.</returns>
    public ConditionalOrder Cancel()
    {
        return Transition(ConditionalStatus.Cancelled);
    }

    /// <summary>Marks the order expired. One-way from pending; a no-op once resolved.</summary>
    /// <returns>The expired order.</returns>
    public ConditionalOrder Expire()
    {
        return Transition(ConditionalStatus.Expired);
    }

    /// <summary>
    /// Marks the order <b>mid-fire</b> — the durable pre-transmit intent (gh#577), recorded before the venue is
    /// touched. One-way from pending; a no-op once resolved. <see cref="ShouldFire"/> and <see cref="ShouldCancel"/>
    /// are both false in this state, so a mid-fire order is inert to the firing pass — it can only be moved on by the
    /// firing service that owns the transmit→journal handoff, never re-decided from a quote.
    /// </summary>
    /// <returns>The order in its firing state.</returns>
    public ConditionalOrder BeginFiring()
    {
        return Transition(ConditionalStatus.Firing);
    }

    private ConditionalOrder Transition(ConditionalStatus to)
    {
        // One-way from Pending: a resolved order never silently changes state, and re-applying a transition is a
        // harmless no-op (a retrying watcher must not re-fire or resurrect).
        return Status == ConditionalStatus.Pending
            ? new ConditionalOrder(Proposal, Trigger, CancelDrift, ExpiresAt, to)
            : this;
    }
}
