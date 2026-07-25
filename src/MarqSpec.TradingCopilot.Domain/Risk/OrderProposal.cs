namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// An order as proposed — by the operator, or by the model on the operator's behalf — <b>before</b> the gate has
/// had its say. Nothing here is authorized; it is the question the gate answers.
/// </summary>
/// <param name="Instrument">The instrument, carrying the tick size and point value the money math needs.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="RequestedQuantity">The contracts asked for. The gate may approve fewer, or none.</param>
/// <param name="Entry">The intended entry price.</param>
/// <param name="Stop">The working stop — the intended exit if the trade is wrong.</param>
/// <param name="SafetyStop">
/// The catastrophic-insurance stop resting beyond the working one. The gate treats the loss at this price as the
/// <b>true worst case</b>, so it — not the working stop — is what the hard account limits are measured against.
/// </param>
/// <param name="ReferencePrice">The current market price, used for the fat-finger check (R-16).</param>
/// <param name="Target">
/// The optional take-profit price — the third bracket leg (ADR-0007, gh#170). It must sit on the <b>winning</b>
/// side of <paramref name="Entry"/> — above it for a long, below it for a short — the mirror of the
/// safety-beyond-actual stop ordering; the execution path refuses a wrong-side target rather than flip it.
/// <see langword="null"/> means no profit leg, which stays valid: the entry rests with its protective stop alone.
/// </param>
public sealed record OrderProposal(
    InstrumentSpec Instrument,
    Venue.OrderSide Side,
    int RequestedQuantity,
    Price Entry,
    Price Stop,
    Price SafetyStop,
    Price ReferencePrice,
    Price? Target = null);
