namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// Whether the dead-man's switch may report "flat" to its external monitor right now (gh#244, R-13, ADR-0019).
/// </summary>
/// <remarks>
/// The monitor pages when a check-in <b>fails to arrive</b>, so <b>silence is the alarm</b> — which makes a
/// wrongly-sent check-in worse than a missing one. The default posture is therefore
/// <see cref="Withhold"/>: the switch reports flat only when it has positively confirmed there is nothing on.
/// </remarks>
public enum CheckInAction
{
    /// <summary>
    /// Say nothing. Either the deadline has not passed yet, or — the case that matters — exposure is still open
    /// past it, and the operator's monitor must be allowed to page.
    /// </summary>
    Withhold,

    /// <summary>
    /// Report flat. The deadline has passed and venue truth says there is no exposure in this instrument, whether
    /// because it was closed or because none was ever open.
    /// </summary>
    Send,

    /// <summary>
    /// Already reported for this market day. The scheduler polls every 15 s; without this the monitor would take
    /// roughly 2,400 identical pings between the deadline and the close.
    /// </summary>
    AlreadySent,

    /// <summary>
    /// No check-in is expected for this market. The operator disabled auto-flatten for it — a deliberate, warned
    /// override (R-13) — so the switch declines to give an all-clear it is not entitled to give, rather than
    /// vouching for a market nothing is watching.
    /// </summary>
    NotApplicable,
}
