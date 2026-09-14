namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The venue-neutral lifecycle state an order-update carries on the account-event stream (R-17, gh#219). The
/// adapter translates the vendor's own vocabulary onto this so the core never sees a venue-specific status.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an unmapped venue status
/// must never read as a real state. The consumer treats terminal non-fill states here (cancelled / expired /
/// rejected) as the order-status source of truth; <b>filled</b> volume comes from the fill events, not this.
/// </remarks>
public enum VenueOrderState
{
    /// <summary>Not a state — the refusable zero. A venue status the adapter could not map lands here.</summary>
    Unknown = 0,

    /// <summary>Accepted and working at the venue.</summary>
    Working = 1,

    /// <summary>Completely filled.</summary>
    Filled = 2,

    /// <summary>Cancelled before completion.</summary>
    Cancelled = 3,

    /// <summary>Expired without completing.</summary>
    Expired = 4,

    /// <summary>Rejected by the venue.</summary>
    Rejected = 5,

    /// <summary>Submitted, not yet working.</summary>
    Pending = 6,
}
