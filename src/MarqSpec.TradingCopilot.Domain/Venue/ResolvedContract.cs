namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue's contract handle <b>together with the instrument it was resolved for</b>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="VenueContractId"/> alone is opaque — <c>projectx:CON.F.US.EP.M25</c> says nothing the domain can
/// check. Carried on its own it lets a caller size an <c>ES</c> proposal and transmit an <c>NQ</c> contract, so
/// the gate authorizes one exposure and the venue receives another. Pairing the two at the point of resolution
/// makes that mismatch detectable instead of silent.
/// </para>
/// <para>
/// The pairing comes from the venue that performed the lookup, which is the only party that knows it. Nothing
/// downstream should construct one by asserting a handle belongs to an instrument.
/// </para>
/// </remarks>
/// <param name="Contract">The venue's contract handle.</param>
/// <param name="Instrument">The instrument this handle was resolved for.</param>
public sealed record ResolvedContract(VenueContractId Contract, InstrumentId Instrument);
