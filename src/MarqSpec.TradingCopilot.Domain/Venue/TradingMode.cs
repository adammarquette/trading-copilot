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
/// So the venue reports its signal, the adapter surfaces it without interpreting it as economic stake, and the
/// <b>operator declares</b> what each stage means at each firm (<see cref="FirmConventions"/>). Firms sharing one
/// platform can and do differ.
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
