using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The <b>one</b> set of authoring refusals over a trigger's condition half (gh#1135 of gh#1059, R-7) — the symbol,
/// comparison, period, resolution, indicator and hysteresis checks
/// <see cref="TriggerEndpoints.CreateTriggerAsync"/> has always made, lifted out so a <i>second</i> author can make
/// exactly the same ones.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the chat <c>edit_rulebook</c> tool authors triggers too, and a second copy of these rules would
/// drift: gh#1007 is the precedent — the same threshold gap had to be fixed twice, at create <b>and</b> at patch —
/// and a model-authored trigger is exactly the caller you least want validated by the older copy.
/// </para>
/// <para>
/// Deliberately <b>one refusal per check</b> rather than one whole-request validator, so each caller keeps its own
/// evaluation <i>order</i>: the create endpoint still refuses a bad route before a bad period, and the tool refuses
/// in the order its own schema reads. Lifting these out therefore moved no decision between callers. The refusal
/// strings are the endpoint's <b>verbatim</b>, so a caller that surfaces them (the API's 400 body) reads exactly as
/// before — pinned by <c>TriggerAuthoringTests</c>, since the endpoint suite asserts only the status.
/// </para>
/// <para>
/// Pure: no I/O, no clock, no database. The account / route / size rules stay with their callers, because they
/// genuinely differ — the agent-review route is the endpoint's alone, and chat authors the mechanical route only.
/// The per-indicator threshold range is <see cref="TriggerThreshold"/>'s and stays there: it is a domain rule about
/// an indicator's semantics rather than an authoring-shape check.
/// </para>
/// </remarks>
internal static class TriggerAuthoring
{
    // The R-22 indicator names a trigger may name (AtrIndicator / RsiIndicator). Case-insensitive in, canonical out,
    // so the stored name is exactly the IIndicatorSource read identity.
    private static readonly IReadOnlyDictionary<string, string> _knownIndicators =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AtrIndicator.IndicatorName] = AtrIndicator.IndicatorName,
            [RsiIndicator.IndicatorName] = RsiIndicator.IndicatorName,
        };

    /// <summary>The canonical indicator names, for a caller that lists them (a tool schema, an error message).</summary>
    public static IReadOnlyCollection<string> KnownIndicators => (IReadOnlyCollection<string>)_knownIndicators.Keys;

    /// <summary>Refuses a blank or unparseable instrument symbol.</summary>
    /// <param name="symbol">The caller-supplied symbol.</param>
    /// <param name="instrument">The parsed instrument when accepted; default otherwise.</param>
    /// <returns>The refusal, or <see langword="null"/> when the symbol is usable.</returns>
    public static string? RefuseSymbol(string? symbol, out InstrumentId instrument) =>
        InstrumentId.TryParse(symbol, out instrument)
            ? null
            : "A trigger needs a non-blank instrument symbol.";

    /// <summary>Refuses the fail-closed <see cref="IndicatorComparison.Unknown"/> zero value.</summary>
    /// <param name="comparison">The caller-supplied comparison.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? RefuseComparison(IndicatorComparison comparison) =>
        comparison == IndicatorComparison.Unknown ? "The comparison must be Below or Above." : null;

    /// <summary>Refuses a non-positive indicator period.</summary>
    /// <param name="period">The caller-supplied period.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? RefusePeriod(int period) => period <= 0 ? "The period must be positive." : null;

    /// <summary>Refuses a non-positive bar resolution.</summary>
    /// <param name="resolutionMinutes">The caller-supplied resolution, in minutes.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? RefuseResolution(int resolutionMinutes) =>
        resolutionMinutes <= 0 ? "The resolution must be a positive number of minutes." : null;

    /// <summary>Refuses an unknown indicator name, resolving the canonical spelling when it is known.</summary>
    /// <param name="indicator">The caller-supplied indicator name (case-insensitive).</param>
    /// <param name="indicatorName">The canonical name when accepted; empty otherwise.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? RefuseIndicator(string? indicator, out string indicatorName)
    {
        if (string.IsNullOrWhiteSpace(indicator) || !_knownIndicators.TryGetValue(indicator, out string? resolved))
        {
            indicatorName = string.Empty;
            return $"Unknown indicator — supported names are {string.Join(", ", _knownIndicators.Keys)}.";
        }

        indicatorName = resolved;
        return null;
    }

    /// <summary>Refuses a non-positive re-arm dead-band; <see langword="null"/> means "no band" and is accepted.</summary>
    /// <param name="hysteresis">The caller-supplied band, or <see langword="null"/> for none.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? RefuseHysteresis(decimal? hysteresis) =>
        hysteresis is <= 0m ? "The hysteresis band must be positive when set — null means none." : null;
}
