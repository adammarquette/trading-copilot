using System.Diagnostics.CodeAnalysis;

namespace MarqSpec.TradingCopilot.Domain;

/// <summary>
/// The configured contract facts for one instrument (gh#541): its <see cref="InstrumentSpec"/> — tick size and point
/// value, the two numbers that turn a price into money — plus the catastrophic <see cref="SafetyStopTicks"/> distance
/// a server-originated proposal is protected by.
/// </summary>
/// <remarks>
/// The safety stop is carried as a <b>distance in ticks</b> rather than a price, because a price is only meaningful
/// against a specific entry. The take path derives the price from the entry and this distance (ADR-0007's
/// catastrophic floor, beyond the working stop); this type supplies the input and computes no order geometry itself.
/// </remarks>
/// <param name="Spec">The tick size and point value.</param>
/// <param name="SafetyStopTicks">How many ticks beyond the entry the catastrophic safety stop sits. Positive.</param>
public sealed record InstrumentContractSpec(InstrumentSpec Spec, int SafetyStopTicks)
{
    /// <summary>The safety stop's distance from the entry, in price terms.</summary>
    public decimal SafetyStopDistance => SafetyStopTicks * Spec.TickSize;
}

/// <summary>
/// Resolves the per-instrument contract facts a <b>server-originated</b> proposal needs to become an order ticket
/// (gh#541, R-4 / R-16, ADR-0007).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A manual ticket carries tick size, point value and the safety stop on the request, because
/// a human authored it. A <see cref="InstrumentSpec"/> is not on a suggestion, and nothing at issuance knows one —
/// so without this seam a suggestion simply cannot be taken. The alternative of letting the <b>client</b> supply the
/// three numbers for a proposal the operator did not author is <b>rejected</b>: it would feed browser-controlled
/// values into the sizing ladder and the fat-finger band, inverting *enforcement lives below the model*.
/// </para>
/// <para>
/// <b>Fail closed.</b> A miss is explicit and must stay that way — never substitute a default tick size, because a
/// fabricated one silently mis-sizes every downstream risk calculation rather than refusing the trade.
/// </para>
/// </remarks>
public interface IInstrumentSpecSource
{
    /// <summary>Resolves one instrument's contract facts.</summary>
    /// <param name="instrument">The venue-neutral instrument (e.g. <c>ES</c>), as a suggestion stores it.</param>
    /// <param name="spec">The resolved facts when configured; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the instrument is configured; <see langword="false"/> on an explicit miss.</returns>
    bool TryResolve(InstrumentId instrument, [NotNullWhen(true)] out InstrumentContractSpec? spec);
}
