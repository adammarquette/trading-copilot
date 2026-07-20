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
}
