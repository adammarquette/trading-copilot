namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// Reports this process's health to a monitor that lives <b>outside</b> it (gh#244, R-13, ADR-0019) — the seam
/// through which silence becomes the alarm.
/// </summary>
/// <remarks>
/// <para>
/// The monitor pages when an expected report <b>fails to arrive</b>, which is the only alerting shape that
/// survives this process dying: an in-process watchdog and an in-stack rule engine both die with it. Nothing here
/// raises an alert; it withholds one, and the absence is what the operator hears about.
/// </para>
/// <para>
/// <b>Implementations must never throw or block for long.</b> This sits beside the one order action the system
/// takes without confirmation, and an alerting fault that breaks or delays a flatten would be precisely the
/// self-inflicted wound ADR-0019 exists to avoid. A failed report returns <see langword="false"/> — which is
/// honest, because a report that did not arrive is exactly what the monitor should page on.
/// </para>
/// </remarks>
public interface IDeadMansSwitch
{
    /// <summary>
    /// Reports that <paramref name="instrument"/> is confirmed flat past its deadline — the daily check-in.
    /// </summary>
    /// <param name="instrument">The instrument being vouched for.</param>
    /// <param name="cancellationToken">The caller's cancellation token; a genuine cancellation propagates.</param>
    /// <returns><see langword="true"/> if the monitor accepted the report.</returns>
    Task<bool> ReportFlatAsync(InstrumentId instrument, CancellationToken cancellationToken);

    /// <summary>
    /// Reports that this process is alive — the liveness heartbeat, unconditional because the deployment is
    /// always-on (R-1). Its absence is what surfaces a host that died mid-session, hours before the deadline
    /// would have revealed it.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token; a genuine cancellation propagates.</param>
    /// <returns><see langword="true"/> if the monitor accepted the report.</returns>
    Task<bool> ReportAliveAsync(CancellationToken cancellationToken);
}
