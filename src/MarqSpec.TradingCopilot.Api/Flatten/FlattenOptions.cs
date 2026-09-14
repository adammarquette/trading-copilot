using System.Globalization;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The per-instrument auto-flatten schedule and how the scheduler host paces itself (gh#185, R-13). Bound from the
/// <c>Flatten</c> config section.
/// </summary>
/// <remarks>
/// <para>
/// Auto-flatten is <b>on by default, per market</b>, and <b>cannot be silently disabled</b> (R-13): the known
/// equity-index / crude / gold products carry built-in default deadlines even with no configuration, so a fresh
/// deployment is armed out of the box. Configuration <i>overrides</i> — a different deadline, or a deliberate,
/// explicit <see cref="FlattenScheduleOption.Enabled"/><c> = false</c> — never a silent absence.
/// </para>
/// <para>
/// The default deadlines are the operator-provided reference times (wiki: market sessions &amp; settlement) —
/// equity-index ~2:30 pm CT ahead of the MOC, crude and gold earlier. They are <b>illustrative and to be confirmed
/// against the CME rulebook</b>; the operator tunes them per account via configuration.
/// </para>
/// </remarks>
public sealed class FlattenOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Flatten";

    /// <summary>
    /// The built-in per-market default deadlines, in market wall-clock (Central) time. Present so auto-flatten is
    /// on by default (R-13). Times are illustrative — confirm per product against the CME rulebook.
    /// </summary>
    private static IReadOnlyDictionary<string, TimeOnly> DefaultDeadlines { get; } =
        new Dictionary<string, TimeOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["ES"] = new(14, 30), // equity-index: ~30 min before the 3:00 pm cash EOD, ahead of the MOC
            ["NQ"] = new(14, 30),
            ["CL"] = new(13, 15), // crude: ahead of the ~1:30 pm settlement
            ["GC"] = new(12, 15), // gold: ahead of the ~12:30 pm settlement
        };

    /// <summary>
    /// How often the host evaluates the schedule. 15 s is fine enough to fire promptly within the one-hour firing
    /// window and to escalate warnings, cheap enough to run always.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 15;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>
    /// How many close attempts a flatten makes before escalating (feeds <see cref="FlattenVerification"/>). Must be
    /// positive — zero would switch the safety net off.
    /// </summary>
    public int MaxFlattenAttempts { get; init; } = 3;

    /// <summary>
    /// The redundant watchdog's grace window (gh#187): how long past an instrument's deadline the watchdog waits
    /// before stepping in — giving the primary scheduler (gh#185) its chance first, so the two tiers do not fight.
    /// The watchdog acts only on the primary's <i>failure</i>: a position still open past deadline + this grace.
    /// </summary>
    public int WatchdogGraceMinutes { get; init; } = 2;

    /// <summary>The watchdog grace window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan WatchdogGrace => TimeSpan.FromMinutes(WatchdogGraceMinutes);

    /// <summary>
    /// How often the redundant watchdog (gh#187) evaluates — its <b>own</b> cadence, independent of the primary's
    /// <see cref="PollIntervalSeconds"/>, so the two tiers never share a timer.
    /// </summary>
    public int WatchdogPollIntervalSeconds { get; init; } = 20;

    /// <summary>The watchdog poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan WatchdogPollInterval => TimeSpan.FromSeconds(WatchdogPollIntervalSeconds);

    /// <summary>Per-instrument overrides of the built-in defaults; may also add an instrument not in the defaults.</summary>
    public FlattenScheduleOption[] Instruments { get; init; } = [];

    /// <summary>
    /// Resolves the effective per-instrument schedules: the built-in defaults, with any configured override
    /// applied on top, plus any configured instrument that is not one of the defaults.
    /// </summary>
    /// <returns>One <see cref="FlattenSchedule"/> per governed instrument.</returns>
    /// <exception cref="FormatException">A configured time is not <c>HH:mm</c>.</exception>
    public IReadOnlyList<FlattenSchedule> ToSchedules()
    {
        // Seed with the on-by-default known products, then let configuration override or extend -- never silently
        // remove one (R-13: disabling is an explicit Enabled=false, applied below, not an omission).
        Dictionary<string, (bool Enabled, TimeOnly Close, TimeOnly? Override)> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, TimeOnly> known in DefaultDeadlines)
        {
            merged[known.Key] = (Enabled: true, Close: known.Value, Override: null);
        }

        foreach (FlattenScheduleOption option in Instruments)
        {
            string symbol = option.Symbol.Trim().ToUpperInvariant();
            TimeOnly? close = ParseTime(option.SessionClose, nameof(option.SessionClose));
            TimeOnly? deadlineOverride = ParseTime(option.DeadlineOverride, nameof(option.DeadlineOverride));

            if (merged.TryGetValue(symbol, out (bool Enabled, TimeOnly Close, TimeOnly? Override) existing))
            {
                merged[symbol] = (option.Enabled, close ?? existing.Close, deadlineOverride);
            }
            else if (close is not null)
            {
                merged[symbol] = (option.Enabled, close.Value, deadlineOverride);
            }

            // An unknown symbol with no session close cannot be scheduled -- it has no deadline to derive. It is
            // not dropped silently: a live position in it is flagged at runtime (flatten.unconfigured, gh#185).
        }

        return
        [
            .. merged.Select(entry => FlattenSchedule.Create(
                InstrumentId.Parse(entry.Key), entry.Value.Enabled, entry.Value.Override, entry.Value.Close)),
        ];
    }

    /// <summary>
    /// Describes the effective schedule per market <b>with its provenance</b>, for the startup report (gh#255).
    /// </summary>
    /// <remarks>
    /// <see cref="ToSchedules"/> deliberately returns only the winner of the merge, which is all the enforcing
    /// path needs. This returns the same resolution plus <i>where each value came from</i> — the one thing that
    /// distinguishes a configured deadline from a built-in default once both are resolved to the same time, and
    /// therefore the only way an operator can see that an override was dropped rather than applied.
    /// </remarks>
    /// <param name="now">The instant whose market date the deadlines are resolved against.</param>
    /// <returns>One report per governed market.</returns>
    /// <exception cref="FormatException">A configured time is not <c>HH:mm</c>.</exception>
    public IReadOnlyList<FlattenScheduleReport> Describe(DateTimeOffset now)
    {
        HashSet<string> configured = ConfiguredSymbols();
        DateOnly marketDate = DateOnly.FromDateTime(MarketClock.ToMarketTime(now));

        return
        [
            .. ToSchedules().Select(schedule => new FlattenScheduleReport(
                schedule.Instrument,
                schedule.Deadline,
                ToUtc(marketDate, schedule.Deadline),
                schedule.Enabled,
                SourceOf(schedule.Instrument.Symbol, configured))),
        ];
    }

    /// <summary>
    /// Describes the effective schedule per market against the <b>next</b> occurrence of each deadline at or
    /// after <paramref name="now"/> (gh#657, R-13) — the read behind the operator's always-visible countdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Describe"/> resolves against the market date of <paramref name="now"/> and stops there, which is
    /// right for a startup report and wrong for a countdown: the moment a deadline is behind us it keeps reporting
    /// an instant in the past, and a countdown fed from it runs negative for the rest of the day.
    /// </para>
    /// <para>
    /// The roll re-resolves the wall-clock deadline against the <i>next date</i> rather than adding 24 hours to
    /// the instant. Those differ by an hour across a daylight-saving change, in the direction that would show the
    /// operator a deadline an hour before the one the scheduler will actually act on.
    /// </para>
    /// <para>
    /// It is a <b>calendar</b> day, not a trading day, and deliberately so: the enforcing path carries no trading
    /// calendar either — it arms against whatever date it is asked about, and a non-trading day simply has no
    /// position to close (<see cref="FlattenCheckIn"/>'s "trivially true on a holiday"). A holiday calendar
    /// invented here would make the countdown disagree with the thing it is counting down to.
    /// </para>
    /// <para>
    /// Each market rolls independently — they do not share a deadline, so they do not share a date.
    /// </para>
    /// </remarks>
    /// <param name="now">The instant the next deadline is resolved relative to.</param>
    /// <returns>One report per governed market, each carrying its next deadline.</returns>
    /// <exception cref="FormatException">A configured time is not <c>HH:mm</c>.</exception>
    public IReadOnlyList<FlattenScheduleReport> DescribeNext(DateTimeOffset now)
    {
        HashSet<string> configured = ConfiguredSymbols();
        DateOnly marketDate = DateOnly.FromDateTime(MarketClock.ToMarketTime(now));

        return
        [
            .. ToSchedules().Select(schedule => new FlattenScheduleReport(
                schedule.Instrument,
                schedule.Deadline,
                NextOccurrence(marketDate, schedule.Deadline, now),
                schedule.Enabled,
                SourceOf(schedule.Instrument.Symbol, configured))),
        ];
    }

    // At-or-after, not strictly after: the flatten fires AT the deadline, so the deadline has not passed until it
    // is behind us. Rolling a moment early would jump the countdown a full day at 00:00 -- the instant the
    // operator is watching it hardest.
    private static DateTimeOffset NextOccurrence(DateOnly marketDate, TimeOnly deadline, DateTimeOffset now)
    {
        DateTimeOffset today = ToUtc(marketDate, deadline);

        return today >= now ? today : ToUtc(marketDate.AddDays(1), deadline);
    }

    // Which markets configuration actually names -- the fact ToSchedules throws away. Compared on the same
    // trimmed, upper-cased key the merge uses, so "es" and " ES " count as naming ES.
    private HashSet<string> ConfiguredSymbols() => Instruments
        .Select(option => option.Symbol.Trim().ToUpperInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static FlattenScheduleSource SourceOf(string symbol, HashSet<string> configured)
    {
        if (!configured.Contains(symbol))
        {
            return FlattenScheduleSource.BuiltInDefault;
        }

        // Configuration named it. Whether that overrode a built-in or added a market is the operator's cue for
        // "did I spell it right" -- a typo'd symbol shows up as an ADDITION beside the untouched default.
        return DefaultDeadlines.ContainsKey(symbol)
            ? FlattenScheduleSource.ConfiguredOverride
            : FlattenScheduleSource.ConfiguredAddition;
    }

    private static DateTimeOffset ToUtc(DateOnly marketDate, TimeOnly deadline)
    {
        DateTime unspecified = DateTime.SpecifyKind(marketDate.ToDateTime(deadline), DateTimeKind.Unspecified);

        // A deadline landing in the spring-forward gap does not exist as a wall-clock time. It cannot happen with
        // afternoon deadlines, but resolving it would silently report a time nobody asked for, so take the zone's
        // offset for the instant instead of inventing one.
        TimeSpan offset = MarketClock.CentralTime.IsInvalidTime(unspecified)
            ? MarketClock.CentralTime.GetUtcOffset(marketDate.ToDateTime(TimeOnly.MinValue))
            : MarketClock.CentralTime.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static TimeOnly? ParseTime(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // A malformed deadline must fail loudly, never coerce to a wrong time on a safety path.
        if (!TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed))
        {
            throw new FormatException($"Flatten '{field}' must be a 24-hour HH:mm market (Central) time, not '{value}'.");
        }

        return parsed;
    }
}

/// <summary>A per-instrument override of the auto-flatten schedule (gh#185, R-13).</summary>
public sealed class FlattenScheduleOption
{
    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>, <c>CL</c>).</summary>
    public required string Symbol { get; init; }

    /// <summary>
    /// Whether auto-flatten is on for this market. On by default; setting it <see langword="false"/> is the
    /// deliberate, warned override (R-13) — never a silent omission.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The instrument's session close in market (Central) <c>HH:mm</c> time; the default deadline derives from it.</summary>
    public string? SessionClose { get; init; }

    /// <summary>An explicit deadline in market (Central) <c>HH:mm</c> time, overriding the session-close default.</summary>
    public string? DeadlineOverride { get; init; }
}
