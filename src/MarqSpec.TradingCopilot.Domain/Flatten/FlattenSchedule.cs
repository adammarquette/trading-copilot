using System.Globalization;

namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// When a market's positions must be closed, and whether the flatten is on for it — configured <b>per
/// instrument</b>, because products settle at different times (R-13).
/// </summary>
/// <remarks>
/// The equity-index default is ~2:30 pm CT: roughly half an hour before the 3:00 pm cash-equity EOD, so the
/// position is out <i>ahead of</i> market-on-close volatility and well before the 4:00 pm CME close. Crude and
/// gold settle earlier and carry earlier deadlines of their own. This fires <b>ahead of</b> any venue-forced
/// flatten — and a live brokerage account has none at all — so it cannot lean on the venue as a backstop.
/// </remarks>
public sealed record FlattenSchedule
{
    /// <summary>How long before the deadline the operator starts being warned.</summary>
    public static readonly TimeSpan WarningWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after the deadline a flatten may still fire. A late tick must not skip the close — but hours
    /// later, into the settlement window, firing blind is worse than escalating.
    /// </summary>
    public static readonly TimeSpan FiringWindow = TimeSpan.FromHours(1);

    private FlattenSchedule(InstrumentId instrument, bool enabled, TimeOnly? deadlineOverride, TimeOnly sessionClose)
    {
        Instrument = instrument;
        Enabled = enabled;
        DeadlineOverride = deadlineOverride;
        SessionClose = sessionClose;
    }

    /// <summary>The instrument this schedule governs.</summary>
    public InstrumentId Instrument { get; }

    /// <summary>
    /// Whether auto-flatten is on for this market. On by default; switching it off is a deliberate, warned
    /// override taken at the operator's own risk (R-13).
    /// </summary>
    public bool Enabled { get; }

    /// <summary>The operator's explicit deadline, or <see langword="null"/> to follow the session close.</summary>
    public TimeOnly? DeadlineOverride { get; }

    /// <summary>The instrument's session close, from which the default deadline derives.</summary>
    public TimeOnly SessionClose { get; }

    /// <summary>The deadline in market wall-clock time — the override where set, else the session close.</summary>
    public TimeOnly Deadline => DeadlineOverride ?? SessionClose;

    /// <summary>Creates a per-instrument flatten schedule.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="enabled">Whether auto-flatten is on for it.</param>
    /// <param name="deadlineOverride">An explicit deadline, or <see langword="null"/> to follow the session close.</param>
    /// <param name="sessionClose">The instrument's session close, in market wall-clock time.</param>
    /// <returns>The schedule.</returns>
    public static FlattenSchedule Create(
        InstrumentId instrument,
        bool enabled,
        TimeOnly? deadlineOverride,
        TimeOnly sessionClose)
    {
        return new FlattenSchedule(instrument, enabled, deadlineOverride, sessionClose);
    }

    /// <summary>Decides what the flatten should do at <paramref name="now"/>.</summary>
    /// <param name="schedule">The instrument's schedule.</param>
    /// <param name="now">The current instant, in any offset — compared in market time.</param>
    /// <param name="hasOpenPosition">Whether the account currently holds exposure in this instrument.</param>
    /// <returns>The decision, carrying the action, the time to the deadline, and the reason.</returns>
    public static FlattenDecision Decide(FlattenSchedule schedule, DateTimeOffset now, bool hasOpenPosition)
    {
        DateTime marketNow = MarketClock.ToMarketTime(now);
        DateTime deadline = marketNow.Date + schedule.Deadline.ToTimeSpan();
        TimeSpan untilDeadline = deadline - marketNow;

        string at = schedule.Deadline.ToString("HH:mm", CultureInfo.InvariantCulture);

        // Checked first, and reported distinctly: a disabled market must never be mistaken for "nothing to do".
        if (!schedule.Enabled)
        {
            return new FlattenDecision(
                FlattenAction.Disabled,
                untilDeadline,
                $"Auto-flatten is disabled for {schedule.Instrument} — positions will not be closed at {at} CT. "
                + "The venue's own forced flatten may still apply on a prop account; a live account has no backstop.");
        }

        if (!hasOpenPosition)
        {
            return new FlattenDecision(
                FlattenAction.None,
                untilDeadline,
                $"No open {schedule.Instrument} position to close.");
        }

        if (untilDeadline > WarningWindow)
        {
            return new FlattenDecision(
                FlattenAction.None,
                untilDeadline,
                $"{schedule.Instrument} flattens at {at} CT.");
        }

        if (untilDeadline > TimeSpan.Zero)
        {
            return new FlattenDecision(
                FlattenAction.Warn,
                untilDeadline,
                $"{schedule.Instrument} flattens at {at} CT — {untilDeadline.TotalMinutes:0} minute(s) away.");
        }

        if (-untilDeadline <= FiringWindow)
        {
            return new FlattenDecision(
                FlattenAction.Flatten,
                untilDeadline,
                $"{schedule.Instrument} deadline {at} CT reached — closing the position.");
        }

        // Past the firing window with exposure still on. Escalate rather than fire blind into the settlement
        // window or, worse, do nothing quietly (ADR-0013).
        return new FlattenDecision(
            FlattenAction.Missed,
            untilDeadline,
            $"MISSED: {schedule.Instrument} deadline {at} CT passed {-untilDeadline.TotalMinutes:0} minute(s) ago "
            + "and a position is still open. The flatten did not fire — escalate and reconcile from the venue.");
    }
}
