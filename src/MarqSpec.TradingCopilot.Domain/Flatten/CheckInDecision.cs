namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// Whether the dead-man's switch reports flat now, and why. The reason travels with the decision because a
/// withheld check-in is the thing that pages the operator — when that happens, the log must already say what the
/// system believed and when (gh#244, ADR-0019).
/// </summary>
/// <param name="Action">Whether to report flat.</param>
/// <param name="MarketDay">
/// The market-time day this decision belongs to — the key a sent check-in is recorded under, so "once per day"
/// means once per <i>market</i> day rather than once per UTC day.
/// </param>
/// <param name="Reason">A human-readable explanation, always populated.</param>
public sealed record CheckInDecision(
    CheckInAction Action,
    DateOnly MarketDay,
    string Reason);
