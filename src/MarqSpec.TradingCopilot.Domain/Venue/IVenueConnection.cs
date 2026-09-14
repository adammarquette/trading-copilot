namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The venue's <b>connection liveness</b> (R-17, ADR-0013): whether the platform's live link to the venue is up.
/// It is the signal the orphan guard watches — on a drop, the in-app synthetic protection a live position relies
/// on (the hidden working stop) can no longer act, so it is orphaned; on reconnect it re-arms (ADR-0007, gh#209).
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> part of the scoped <see cref="ITradingVenue"/>: the connection is <b>process-wide</b>
/// (one credential set per process, ADR-0015), so this is a singleton seam over the process's venue client, not a
/// per-request view. A data-only provider with no persistent link simply does not register one.
/// </remarks>
public interface IVenueConnection
{
    /// <summary>The venue this connection speaks to — tags every orphan/re-arm the guard records.</summary>
    VenueId Venue { get; }

    /// <summary>
    /// Whether the live venue link is currently up. A drop here means quotes stop flowing, so a hidden stop can
    /// no longer be promoted — the guard's cue to orphan the synthetic protection (the native safety stop stays
    /// the floor regardless).
    /// </summary>
    bool IsConnected { get; }
}
