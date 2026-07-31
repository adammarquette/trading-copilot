namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Reads a market's session deadline — the Central wall-clock time its positions must be closed by (gh#544, R-13) —
/// for callers that need to <b>respect</b> the deadline without being able to act on it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the agent-review path must not depend on the auto-flatten machinery. That is enforced
/// structurally: <c>AgentReviewGateBelowModelTests</c> fails the build if anything on the path takes a constructor
/// dependency whose type name matches <c>Flatten</c> (among others), because those types cancel working orders and
/// close positions. Injecting the flatten options to read a deadline would trip that guard — correctly, since the
/// guard is about capability, not naming.
/// </para>
/// <para>
/// So the seam is deliberately <b>narrow and read-only</b>: it hands back a <see cref="TimeOnly"/> and nothing else.
/// No flatten type crosses it, there is no operation to invoke, and a caller physically cannot flatten anything
/// through it. Renaming a flatten dependency to slip past the guard would be an evasion; reducing the dependency to
/// the one immutable fact actually needed is the fix.
/// </para>
/// </remarks>
public interface ISessionDeadlineSource
{
    /// <summary>The market's deadline in Central wall-clock time.</summary>
    /// <param name="instrument">The venue-neutral instrument (e.g. <c>ES</c>).</param>
    /// <returns>
    /// The deadline, or <see langword="null"/> when there is none to respect — an unknown market, or one whose
    /// auto-flatten the operator deliberately disabled (R-13 makes disabling explicit and warned).
    /// </returns>
    TimeOnly? DeadlineFor(InstrumentId instrument);
}
