namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The engaged state of the kill switch (ADR-0007, gh#189), as read by the enforcing send path. While engaged the
/// system's outbound orders are disabled, so every transmission is refused <b>above</b> the normal flow — before
/// an order is even sized.
/// </summary>
/// <remarks>
/// The domain only needs to <i>read</i> the state. The setter side — engaging / disengaging, the persisted lock
/// that survives a restart, cancelling working orders, and flattening — lives in the composition layer; keeping
/// the reader an interface here keeps <see cref="OrderExecutionService"/> pure and lets the gate be tested with a
/// fake in either state.
/// </remarks>
public interface IKillSwitch
{
    /// <summary>Whether the kill switch is engaged — outbound orders are disabled.</summary>
    bool IsEngaged { get; }
}
