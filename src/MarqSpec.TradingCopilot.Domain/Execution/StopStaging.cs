namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Where a protective stop physically rests (ADR-0007, gh#11).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an uninitialised
/// staging state must never read as "already at the exchange".
/// </remarks>
public enum StopStaging
{
    /// <summary>Not a staging state — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>
    /// Held <b>synthetically</b> by the platform — not on the book, so the entry cannot be anticipated or
    /// stop-hunted. Depends on the platform being live, which is why the safety stop rests natively beyond it.
    /// </summary>
    Hidden = 1,

    /// <summary>Promoted to a <b>native working order</b> at the venue — exchange-held, survives an outage.</summary>
    Native = 2,
}
