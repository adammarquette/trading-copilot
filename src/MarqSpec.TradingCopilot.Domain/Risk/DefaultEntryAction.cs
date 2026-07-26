namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Which entry action is the operator's <b>default</b> — the primary half of the Approve split button (R-11,
/// ADR-0007, gh#218).
/// </summary>
/// <remarks>
/// <see cref="ApproveAndArm"/> is the zero value on purpose — the fail-safe default is review-first, so an unset
/// or defaulted profile (including one written before this preference existed) resolves to arming a ticket for
/// review, never to the one-action send. This is the same zero-is-the-safe-default pattern as
/// <c>KillSwitchMode.FlattenAll</c> — not the refusable-<c>Unknown</c> pattern, so there is no
/// <c>&lt;&gt; 0</c> check to refuse the default. Defaulting to <see cref="SendAsIs"/> is a deliberate opt-in,
/// allowed on <b>practice accounts only</b> and only with an explicit confirmation; the enforcement floor is
/// unchanged either way — send-as-is skips the manual review, never R-5 / R-16 / R-12.
/// </remarks>
public enum DefaultEntryAction
{
    /// <summary>Approve &amp; arm an editable ticket for review; sending stays a separate, explicit action. The default.</summary>
    ApproveAndArm,

    /// <summary>Send the suggestion in one action, skipping the manual review (practice-only, confirm-to-enable).</summary>
    SendAsIs,
}
