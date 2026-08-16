namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Whether <b>capital is at risk</b> on an account — the axis R-14 gates on.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> what a venue's mode flag reports. ProjectX's <c>simulated</c> and Tradovate's demo/live
/// host say <i>where an order executes</i>; on a prop platform that is nearly orthogonal to what is at stake. A
/// funded Topstep account executes on a simulated engine yet a breach costs a real payout.
/// </para>
/// <para>
/// So at a <b>prop firm</b> the venue's own flag is <b>not consulted</b> at all, and the <b>operator declares</b>
/// what each stage means at each firm (<see cref="FirmConventions"/>). Firms sharing one platform can and do
/// differ.
/// </para>
/// <para>
/// <b>At a brokerage that reasoning does not apply, and the flag is authoritative</b> (gh#780). The rule above
/// exists because a prop-firm <i>funded</i> account executes on a simulated engine while a real payout is at
/// stake — the flag lies there. At a brokerage a paper account <i>is</i> practice and a live account <i>is</i>
/// live, so no declaration the operator could type would be more reliable than the venue's own answer; it could
/// only disagree with it. <see cref="FirmConventions.ModeFollowsVenue"/> carries which rule a firm is under, and
/// the flag reaches resolution through <see cref="FirmConventions.ModeFor(AccountStage, bool?)"/> — the adapter
/// passes it, the conventions decide whether it may be read.
/// </para>
/// <para>
/// That is the <b>scope</b> of "never derive mode from the venue flag", not an exception to it: extending a
/// safety rule past the case it was written for is how it becomes superstition — and here it cost every
/// discovered brokerage account its tradeability, permanently and with no operator remedy.
/// </para>
/// </remarks>
public enum TradingMode
{
    /// <summary>
    /// Not yet classified — the firm has not said what this stage means. Tradeable in <b>no</b> environment.
    /// Deliberately the zero value: an uninitialised mode must fail closed, never default to something tradeable.
    /// </summary>
    Undeclared = 0,

    /// <summary>
    /// The firm has declared this stage carries <b>no capital at risk</b>. The execution path is still real.
    /// </summary>
    Practice = 1,

    /// <summary>
    /// <b>Capital is at risk</b> — a live brokerage account, or a funded prop account whose breach costs money.
    /// Permitted in production only (R-14, <see cref="TradingModePolicy"/>).
    /// </summary>
    Live = 2,
}
