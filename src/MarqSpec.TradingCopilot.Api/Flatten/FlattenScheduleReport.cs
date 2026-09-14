using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// Where an instrument's effective flatten schedule came from (gh#255, R-13). Reported at startup because the
/// deadline alone cannot answer it: a configured value and a built-in default look identical once resolved, so
/// only the source distinguishes "my override took" from "my override was dropped and the default is running".
/// </summary>
public enum FlattenScheduleSource
{
    /// <summary>Not determined — the fail-closed zero, never a legitimate reported value.</summary>
    Unknown = 0,

    /// <summary>No configuration entry names this market; it runs on its built-in deadline (R-13, on by default).</summary>
    BuiltInDefault = 1,

    /// <summary>A configuration entry names a market that also has a built-in deadline, overriding it.</summary>
    ConfiguredOverride = 2,

    /// <summary>A configuration entry adds a market that carries no built-in deadline.</summary>
    ConfiguredAddition = 3,
}

/// <summary>
/// One market's effective auto-flatten schedule, as resolved at startup and reported to the operator (gh#255).
/// </summary>
/// <remarks>
/// This is a <b>reporting</b> view, not the enforcing one — <see cref="FlattenSchedule"/> remains what the
/// scheduler and watchdog act on. It exists because provenance is lost by the time a schedule is resolved:
/// <see cref="FlattenOptions.ToSchedules"/> merges built-in defaults with configuration and returns the winner,
/// with nothing left to say which side supplied it.
/// </remarks>
/// <param name="Instrument">The market this schedule governs.</param>
/// <param name="Deadline">The effective deadline in market wall-clock (Central) time.</param>
/// <param name="DeadlineUtc">
/// The same deadline resolved to an instant for the reported date. Reported alongside the wall-clock time
/// because the two diverge by an hour across daylight saving, and an operator reading UTC logs cannot convert it
/// in their head reliably — the drift that would land a flatten after the close it exists to beat.
/// </param>
/// <param name="Enabled">Whether auto-flatten is armed for this market.</param>
/// <param name="Source">Whether the deadline came from configuration or the built-in default.</param>
public sealed record FlattenScheduleReport(
    InstrumentId Instrument,
    TimeOnly Deadline,
    DateTimeOffset DeadlineUtc,
    bool Enabled,
    FlattenScheduleSource Source);
