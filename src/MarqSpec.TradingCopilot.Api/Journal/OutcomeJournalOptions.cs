namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>How the Outcome writer runs (gh#909, R-9). Bound from the <c>OutcomeJournal</c> config section.</summary>
/// <remarks>
/// <b>No work list here</b>, like <c>IndicatorOptions</c>: the writer derives its work from the journal — whatever
/// closed trade lacks an outcome gets one — so there is nothing to keep in sync and drift apart from.
/// </remarks>
public sealed class OutcomeJournalOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "OutcomeJournal";

    /// <summary>
    /// How often the writer sweeps for closed trades that need an outcome. Outcomes feed reports and the learning set
    /// (gh#21 / gh#22), never the execution path, so they are not time-critical — a few minutes is ample, and a slow
    /// cadence keeps the sweep off the hot path.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 300;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
