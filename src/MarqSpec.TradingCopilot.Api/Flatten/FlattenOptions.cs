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
