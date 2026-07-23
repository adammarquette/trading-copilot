namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The lifecycle state of a journaled order (data dictionary §4).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is deliberately the zero value so an uninitialised status is refusable — the journal's
/// DB check rejects it — rather than silently reading as a real state (the fail-closed-zero pattern, gh#60).
/// </remarks>
public enum OrderStatus
{
    /// <summary>Not a status — the refusable zero. Never journaled.</summary>
    Unknown = 0,

    /// <summary>Accepted and working at the venue.</summary>
    Working = 1,

    /// <summary>Partially filled; a remainder is still working.</summary>
    PartiallyFilled = 2,

    /// <summary>Completely filled.</summary>
    Filled = 3,

    /// <summary>Cancelled before completion.</summary>
    Cancelled = 4,

    /// <summary>Rejected by the venue or the risk gate.</summary>
    Rejected = 5,

    /// <summary>
    /// Armed and staged server-side (gh#11, ADR-0007) — gate-evaluated, editable, <b>not at the venue</b>.
    /// Taking it re-validates everything against fresh truth (R-12) before it becomes <see cref="Working"/>.
    /// </summary>
    Staged = 6,
}
