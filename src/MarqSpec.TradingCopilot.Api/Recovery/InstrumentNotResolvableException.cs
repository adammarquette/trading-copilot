using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Raised by the instrument-scoped venue-truth reads (gh#772) when a syntactically-valid instrument resolves to
/// <b>no venue contract</b> — a <b>client</b> mistake (a typo'd or delisted symbol), which the endpoints map to a
/// <b>400</b>.
/// </summary>
/// <remarks>
/// It exists to keep "the client asked for a nonexistent instrument" distinct from "the venue could not be
/// reached". The read reaches the venue (roster + book) <i>before</i> resolving the scope contract, so a resolve
/// failure at that point is the venue answering "no such contract", not a transport fault — a distinct, narrow
/// signal, so the reads' broad catch still maps genuine unreachability to <c>PositionMarkBasis.Unknown</c>, which
/// means specifically "the venue could not be reached" (ADR-0013). Conflating the two would let a typo'd instrument
/// read as a venue outage, and vice versa.
/// </remarks>
public sealed class InstrumentNotResolvableException : Exception
{
    /// <summary>Creates the exception for an instrument that resolved to no contract.</summary>
    /// <param name="instrument">The instrument the caller asked to scope to.</param>
    /// <param name="innerException">The venue's own not-found exception.</param>
    public InstrumentNotResolvableException(InstrumentId instrument, Exception innerException)
        : base($"No contract matches instrument '{instrument}'.", innerException)
    {
        Instrument = instrument;
    }

    /// <summary>The instrument that did not resolve to a venue contract.</summary>
    public InstrumentId Instrument { get; }
}
