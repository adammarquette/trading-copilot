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

    /// <summary>
    /// A take is <b>in flight</b> for this staged ticket (gh#530) — claimed by one request, not yet resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim exists because the take path spans four venue round-trips between reading the row and writing the
    /// result. Two concurrent takes each read <see cref="Staged"/>, each passed the check against their own
    /// snapshot, and <b>both transmitted</b> — then both wrote the same row, so one live venue order ended up
    /// recorded on no <c>Order</c> row at all. Untracked exposure: invisible to cancel, to the kill-switch sweep
    /// and to the orphan guard, all of which resolve by row id.
    /// </para>
    /// <para>
    /// <b>Transient, never a resting state.</b> It exists only inside one request: the winner moves it to
    /// <see cref="Working"/> on a placed order, or back to <see cref="Staged"/> on any other outcome, so a refused
    /// take leaves the ticket exactly as takeable as it was. A row found sitting here means the process died
    /// mid-take — the one case worth looking at, and loud rather than silent.
    /// </para>
    /// <para>
    /// Every other status branch in the codebase is an explicit allow-list (<see cref="Working"/>,
    /// <see cref="PartiallyFilled"/>, <see cref="Staged"/>), so this value is inert to them by construction — and
    /// deliberately so for <b>cancel</b>, which now refuses a ticket whose take is mid-flight rather than writing
    /// <see cref="Cancelled"/> over a row the take is about to overwrite back to <see cref="Working"/>.
    /// </para>
    /// </remarks>
    Taking = 7,
}
