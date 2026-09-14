using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The staged-stop plan for one position (ADR-0007, gh#11): a <b>hidden actual stop</b> promoted to a native
/// working order once price comes within a configured proximity, with an <b>always-native safety stop</b>
/// resting beyond it as catastrophic insurance.
/// </summary>
/// <remarks>
/// <para>
/// The invariant this type exists to hold is <b>safety-beyond-actual</b>: the safety stop must sit further from
/// entry than the actual stop. If it were nearer it would trigger first, and the per-trade worst case — which
/// ADR-0007 makes physical at the max-drawdown-per-trade — would be neither deterministic nor the one declared.
/// </para>
/// <para>
/// Immutable: <see cref="Promote"/> returns a new plan. The <i>watcher</i> feeds live price into
/// <see cref="ShouldPromote"/> and transmits the promoted order; this type is the deterministic decision it
/// consults.
/// </para>
/// <para>
/// <b>This type does not know what a proximity metric means (gh#311).</b> It carries <see cref="Proximity"/> as
/// persisted configuration and compares a price to a <i>distance the caller resolved</i> — because an ATR band
/// needs a measurement no pure, database-reconstructable value object should be reaching out for, and a band
/// fixed at construction would silently go stale as ATR moved. <see cref="StopProximity.ResolveDistance"/> is
/// where a metric becomes a number.
/// </para>
/// </remarks>
public sealed record StopPlan
{
    private StopPlan(
        InstrumentSpec instrument,
        OrderSide side,
        Price entry,
        Price actualStop,
        Price safetyStop,
        StopProximity proximity,
        StopStaging staging)
    {
        Instrument = instrument;
        Side = side;
        Entry = entry;
        ActualStop = actualStop;
        SafetyStop = safetyStop;
        Proximity = proximity;
        Staging = staging;
    }

    /// <summary>The instrument, carried for its tick size — the band is measured in the instrument's units.</summary>
    public InstrumentSpec Instrument { get; }

    /// <summary>The position's side. Determines which way "beyond" and "closer" run.</summary>
    public OrderSide Side { get; }

    /// <summary>The entry price the stops are measured from.</summary>
    public Price Entry { get; }

    /// <summary>The working stop — hidden until promoted.</summary>
    public Price ActualStop { get; }

    /// <summary>The catastrophic-insurance stop, always native, resting beyond <see cref="ActualStop"/>.</summary>
    public Price SafetyStop { get; }

    /// <summary>How close price must come to <see cref="ActualStop"/> before promotion.</summary>
    public StopProximity Proximity { get; }

    /// <summary>Where the actual stop currently rests.</summary>
    public StopStaging Staging { get; }

    /// <summary>Creates a plan with the actual stop <b>hidden</b>, validating every invariant.</summary>
    /// <param name="instrument">The instrument being traded.</param>
    /// <param name="side">The position side.</param>
    /// <param name="entry">The entry price.</param>
    /// <param name="actualStop">The working stop; must sit on the losing side of <paramref name="entry"/>.</param>
    /// <param name="safetyStop">The safety stop; must sit <b>beyond</b> <paramref name="actualStop"/>.</param>
    /// <param name="proximity">The promotion band.</param>
    /// <returns>The hidden-stage plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A stop sits on the wrong side, or safety is not beyond actual.</exception>
    public static StopPlan Create(
        InstrumentSpec instrument,
        OrderSide side,
        Price entry,
        Price actualStop,
        Price safetyStop,
        StopProximity proximity)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(proximity);

        // Both stops must sit on the losing side of entry, and the safety stop strictly beyond the actual one.
        bool ordered = side switch
        {
            OrderSide.Buy => safetyStop.Value < actualStop.Value && actualStop.Value < entry.Value,
            OrderSide.Sell => safetyStop.Value > actualStop.Value && actualStop.Value > entry.Value,
            _ => false, // an unrecognized side is refused, never assumed (the fail-open lesson)
        };

        if (!ordered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(safetyStop),
                safetyStop.Value,
                $"A {side} plan needs entry {entry.Value}, actual stop {actualStop.Value}, and safety stop "
                + $"{safetyStop.Value} ordered so the actual stop is between them — the safety stop is the "
                + "catastrophic floor and must sit beyond the working stop, or it triggers first.");
        }

        return new StopPlan(instrument, side, entry, actualStop, safetyStop, proximity, StopStaging.Hidden);
    }

    /// <summary>
    /// Whether price has come close enough to promote the hidden stop to a native working order. Always
    /// <see langword="false"/> once already <see cref="StopStaging.Native"/> — nothing left to promote, and a
    /// retrying watcher must not re-transmit an order already resting at the venue.
    /// </summary>
    /// <param name="currentPrice">The current market price.</param>
    /// <param name="bandDistance">
    /// The promotion band as an <b>absolute price distance</b>, already resolved by the caller — see
    /// <see cref="StopProximity.ResolveDistance"/>. A caller that could not resolve one must not call this at
    /// all: there is no value here that means "unmeasurable", because zero is a real band meaning
    /// <i>promote on touch</i>.
    /// </param>
    /// <returns><see langword="true"/> when the stop should be promoted now.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bandDistance"/> is negative.</exception>
    public bool ShouldPromote(Price currentPrice, decimal bandDistance)
    {
        if (bandDistance < 0m)
        {
            // Every metric resolves to a non-negative distance, so this is a logic error rather than a
            // configuration one -- and a negative band would quietly hold the stop hidden until price was already
            // PAST it, protecting later than the plan says. Refuse rather than silently protect late.
            throw new ArgumentOutOfRangeException(
                nameof(bandDistance), bandDistance, "A promotion band cannot be a negative distance.");
        }

        if (Staging != StopStaging.Hidden)
        {
            return false;
        }

        // Promote once price is within the band of the stop -- or already through it.
        return Side switch
        {
            OrderSide.Buy => currentPrice.Value <= ActualStop.Value + bandDistance,
            OrderSide.Sell => currentPrice.Value >= ActualStop.Value - bandDistance,
            _ => false,
        };
    }

    /// <summary>
    /// Promotes the actual stop to native. One-way and idempotent: a promoted plan never silently reverts to
    /// hidden, and re-promoting is a harmless no-op.
    /// </summary>
    /// <returns>The promoted plan.</returns>
    public StopPlan Promote()
    {
        return Staging == StopStaging.Native
            ? this
            : new StopPlan(Instrument, Side, Entry, ActualStop, SafetyStop, Proximity, StopStaging.Native);
    }
}
