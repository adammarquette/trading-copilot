namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// What the flatten should do now, and why. Every auto-flatten action is journaled (R-13), so the reason travels
/// with the decision rather than being reconstructed after the fact.
/// </summary>
/// <param name="Action">The action to take.</param>
/// <param name="TimeUntilDeadline">
/// How long until the deadline — negative once it has passed. Drives the escalating warnings.
/// </param>
/// <param name="Reason">A human-readable explanation, always populated.</param>
public sealed record FlattenDecision(
    FlattenAction Action,
    TimeSpan TimeUntilDeadline,
    string Reason);
