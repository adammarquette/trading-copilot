namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>The result of checking whether a flatten actually left the account flat (R-13).</summary>
public enum FlattenVerdict
{
    /// <summary>The venue reports no remaining exposure. Done.</summary>
    Flat,

    /// <summary>Exposure remains and attempts are left. Close again.</summary>
    Retry,

    /// <summary>
    /// Exposure remains and the attempts are spent. Escalate loudly — a position surviving the flatten is
    /// precisely what the feature exists to prevent, so this must never end as a silent give-up.
    /// </summary>
    Escalate,
}
