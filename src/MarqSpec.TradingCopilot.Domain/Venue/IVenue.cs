namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// What every venue-facing source has in common: who it is, and what it can do. The base of the three R-17
/// slices — market data, accounts, execution.
/// </summary>
public interface IVenue
{
    /// <summary>The venue's identifier, used to tag everything that crosses the seam.</summary>
    VenueId Id { get; }

    /// <summary>What this venue supports. Ask before relying on a capability.</summary>
    VenueCapabilities Capabilities { get; }

    /// <summary>
    /// The version of this adapter's <b>derivation logic</b> — the code that turns raw venue facts into derived
    /// values (stage from a name, mode from conventions, buying power). Bumped whenever that behaviour changes,
    /// so a persisted snapshot stamped with it stays interpretable after the logic moves on (ADR-0009, the
    /// <c>adapter_logic_version</c> the data dictionary records on <c>AccountSnapshot</c>).
    /// </summary>
    int AdapterLogicVersion { get; }
}
