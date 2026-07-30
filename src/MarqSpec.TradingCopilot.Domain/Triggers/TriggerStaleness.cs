namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Whether a trigger has been unevaluable long enough to be reported (gh#469): the outage clock, and the decision
/// to speak up about it.
/// </summary>
/// <param name="UnmeasurableSince">
/// When the current run of unmeasurable readings began, or <see langword="null"/> when the last reading was
/// measurable.
/// </param>
/// <param name="ReportedAt">
/// When this outage was reported, or <see langword="null"/> if it has not been. Cleared with
/// <paramref name="UnmeasurableSince"/> on recovery, so a later outage is a new incident (gh#515).
/// </param>
/// <param name="ShouldReport">Whether this pass is the one that reports the outage — true at most once per outage.</param>
/// <remarks>
/// <para>
/// <b>The fail-closed half already worked.</b> A null indicator reads <see cref="ConditionSatisfaction.Unmeasurable"/>
/// and <see cref="TriggerDebounce"/> holds — never a fire, never a re-arm. What was missing is that nothing said
/// so: a trigger whose dependency stopped being produced (the symbol left <c>Ingestion:Symbols</c>, the resolution
/// changed, the indicator left <c>IndicatorSet</c>) just stopped firing, and <i>"the condition never occurred"</i>
/// was indistinguishable from <i>"unevaluable for a week"</i>.
/// </para>
/// <para>
/// <b>The distinguishing fact is duration</b>, which is why this needs persisted state rather than a log line per
/// pass. One missed scan is a late bar and entirely normal; a day of them is a broken trigger. Reporting every
/// pass would also be a log line per trigger per poll — noise nobody reads, which is the same alert-fatigue
/// failure ADR-0019's noise budget exists to prevent.
/// </para>
/// <para>
/// The same shape as the ATR promotion band (gh#311): an unmeasurable ATR does not promote, is not silently
/// treated as a zero band, and the watcher <b>logs a warning</b> — because "never promoted" is otherwise
/// indistinguishable from "price never came close".
/// </para>
/// <para>
/// <b>It never disables a trigger.</b> Reporting is the whole remit. Auto-disabling would turn a data-pipeline
/// hiccup into a silently disarmed alert, which is strictly worse than the problem being solved.
/// </para>
/// </remarks>
public readonly record struct TriggerStaleness(
    DateTimeOffset? UnmeasurableSince,
    DateTimeOffset? ReportedAt,
    bool ShouldReport)
{
    /// <summary>
    /// How long a trigger must be continuously unevaluable before it is reported.
    /// </summary>
    /// <remarks>
    /// A constant rather than configuration, deliberately: a new configuration key must be added to
    /// <c>.env.example</c> <b>and</b> the compose <c>environment:</c> allowlist or it is silently dropped
    /// (gh#236 / gh#325), and nothing here needs operator tuning. Thirty minutes spans many scans at the default
    /// 60-second poll, so a late bar — or a run of them — never trips it, while a dependency that genuinely
    /// stopped being produced is surfaced within the hour.
    /// </remarks>
    public static TimeSpan ReportAfter { get; } = TimeSpan.FromMinutes(30);

    /// <summary>Advances the outage clock for one evaluation.</summary>
    /// <param name="unmeasurableSince">The trigger's stored outage start, or <see langword="null"/> if measurable.</param>
    /// <param name="reportedAt">
    /// The trigger's stored report instant, or <see langword="null"/> if this outage has not been reported yet —
    /// the bit that keeps a crossed threshold from re-reporting every pass (gh#515).
    /// </param>
    /// <param name="satisfaction">What this pass's reading evaluated to.</param>
    /// <param name="now">The instant of this pass.</param>
    /// <returns>The next outage state, and whether to report.</returns>
    public static TriggerStaleness Track(
        DateTimeOffset? unmeasurableSince,
        DateTimeOffset? reportedAt,
        ConditionSatisfaction satisfaction,
        DateTimeOffset now)
    {
        // Only Unmeasurable means "the dependency did not resolve". Indeterminate is a hysteresis dead-band: the
        // indicator WAS measured and the level simply sits inside the band, which is a working trigger holding
        // position. Treating the two alike would report every hysteresis trigger as broken.
        if (satisfaction != ConditionSatisfaction.Unmeasurable)
        {
            // Both halves clear together. The report must not outlive its outage, or a trigger that broke,
            // recovered, and broke again would never be reported the second time (gh#497's shape, in miniature).
            return new TriggerStaleness(null, null, ShouldReport: false);
        }

        // The clock keeps its original start, so the outage duration grows. Restarting it each pass would mean the
        // threshold is never crossed and the trigger stays silently inert forever -- the defect itself.
        DateTimeOffset since = unmeasurableSince ?? now;

        // gh#515: the threshold alone is not the trigger to speak -- `now - since >= ReportAfter` stays true for
        // every subsequent pass, which made this ~1,440 identical warnings per trigger per day at the 60s poll,
        // the exact alert fatigue this type's own doc claims to avoid. Having-already-reported is the missing
        // durable bit, and it is persisted rather than in-memory so a restart mid-outage does not re-report.
        bool shouldReport = reportedAt is null && now - since >= ReportAfter;

        return new TriggerStaleness(since, shouldReport ? now : reportedAt, shouldReport);
    }
}
