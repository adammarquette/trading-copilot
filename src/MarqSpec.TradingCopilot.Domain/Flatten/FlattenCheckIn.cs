using System.Globalization;

namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// The <b>dead-man's switch</b> for the auto-flatten (gh#244, R-13, ADR-0013, ADR-0019): decides when the system
/// has earned the right to tell an <i>external</i> monitor that a market is flat.
/// </summary>
/// <remarks>
/// <para>
/// Every other alert in this system assumes something is alive to raise it, and the worst R-13 failure does not
/// satisfy that assumption: <b>if the host dies before the deadline, the flatten never fires and nothing alerts</b>
/// — the in-process watchdog (gh#187) and any in-stack rule engine die with it. ADR-0013 already made this
/// argument about schedulers ("a tier outage takes it down too"); it applies unchanged to the thing meant to
/// report the scheduler failed.
/// </para>
/// <para>
/// So the direction is inverted. The app <b>checks in</b>, an external monitor expects that check-in on a
/// schedule, and it pages when the check-in does not arrive: <b>silence becomes the alarm</b>. The consequence for
/// this type is that a wrongly-sent check-in is worse than a missing one — it tells the operator the position was
/// closed while it is still on, and it is believed. The default is therefore
/// <see cref="CheckInAction.Withhold"/>; flat is asserted only when positively confirmed.
/// </para>
/// <para>
/// <b>No exchange calendar is needed.</b> A check-in means "the deadline has passed and there is no exposure in
/// this instrument" — which on a holiday is trivially true, so a non-trading day checks in and stays quiet. Which
/// days a check-in is <i>expected</i> is the monitor's schedule to hold, not this system's.
/// </para>
/// </remarks>
public static class FlattenCheckIn
{
    /// <summary>
    /// Whether a check-in could possibly be sent for this instrument at <paramref name="now"/> — the cheap gate
    /// that decides if venue truth is worth fetching at all.
    /// </summary>
    /// <remarks>
    /// Purely an efficiency guard in front of <see cref="Decide"/>, which remains the authority: without it the
    /// service would read venue positions once a minute all afternoon for an instrument that already reported
    /// flat. It deliberately answers only the questions that need no venue call — enabled, past the deadline, not
    /// already sent today — and never the exposure question.
    /// </remarks>
    /// <param name="schedule">The instrument's flatten schedule.</param>
    /// <param name="now">The current instant, in any offset.</param>
    /// <param name="lastCheckedInOn">The market day a check-in was last sent for this instrument, if any.</param>
    /// <returns><see langword="true"/> when venue truth should be fetched and <see cref="Decide"/> consulted.</returns>
    public static bool IsDue(FlattenSchedule schedule, DateTimeOffset now, DateOnly? lastCheckedInOn)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.Enabled)
        {
            return false;
        }

        DateTime marketNow = MarketClock.ToMarketTime(now);
        if (marketNow < marketNow.Date + schedule.Deadline.ToTimeSpan())
        {
            return false;
        }

        return lastCheckedInOn != DateOnly.FromDateTime(marketNow);
    }

    /// <summary>
    /// Decides whether to report this instrument flat at <paramref name="now"/>.
    /// </summary>
    /// <param name="schedule">The instrument's flatten schedule — supplies the deadline and the enabled flag.</param>
    /// <param name="now">The current instant, in any offset — compared in market wall-clock time.</param>
    /// <param name="hasOpenPosition">
    /// Whether venue truth reports exposure in this instrument right now. Must come from the venue, never a local
    /// belief (ADR-0013) — a check-in derived from a stale local snapshot would assert flatness nobody verified.
    /// </param>
    /// <param name="lastCheckedInOn">The market day a check-in was last sent for this instrument, if any.</param>
    /// <returns>The decision, carrying the action, the market day it belongs to, and the reason.</returns>
    public static CheckInDecision Decide(
        FlattenSchedule schedule,
        DateTimeOffset now,
        bool hasOpenPosition,
        DateOnly? lastCheckedInOn)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        DateTime marketNow = MarketClock.ToMarketTime(now);
        DateOnly marketDay = DateOnly.FromDateTime(marketNow);
        DateTime deadline = marketNow.Date + schedule.Deadline.ToTimeSpan();
        TimeSpan untilDeadline = deadline - marketNow;

        string at = schedule.Deadline.ToString("HH:mm", CultureInfo.InvariantCulture);

        // Checked first: the operator switched the safety net off for this market, so the switch has nothing to
        // vouch for. Reporting flat here would be an all-clear about a market nothing is watching.
        if (!schedule.Enabled)
        {
            return new CheckInDecision(
                CheckInAction.NotApplicable,
                marketDay,
                $"Auto-flatten is disabled for {schedule.Instrument} — no check-in is expected for it. "
                + "Pause this market's monitor check, or the absent check-in will page.");
        }

        // Before the deadline nothing is due. Flat now says nothing about flat then -- a position can still be
        // opened in between.
        if (untilDeadline > TimeSpan.Zero)
        {
            return new CheckInDecision(
                CheckInAction.Withhold,
                marketDay,
                $"{schedule.Instrument} flattens at {at} CT — {untilDeadline.TotalMinutes:0} minute(s) away; "
                + "no check-in is due until the deadline has passed.");
        }

        // The load-bearing branch. Exposure past the deadline is exactly the condition the monitor exists to page
        // on, so the switch must stay silent and let it.
        if (hasOpenPosition)
        {
            return new CheckInDecision(
                CheckInAction.Withhold,
                marketDay,
                $"{schedule.Instrument} deadline {at} CT passed {-untilDeadline.TotalMinutes:0} minute(s) ago and "
                + "exposure is still open — withholding the check-in so the monitor pages.");
        }

        if (lastCheckedInOn == marketDay)
        {
            return new CheckInDecision(
                CheckInAction.AlreadySent,
                marketDay,
                $"{schedule.Instrument} already reported flat for {marketDay:yyyy-MM-dd}.");
        }

        return new CheckInDecision(
            CheckInAction.Send,
            marketDay,
            $"{schedule.Instrument} confirmed flat at the venue after its {at} CT deadline.");
    }
}
