namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Where an account sits in a firm's programme. Read from what the venue reports — for ProjectX that means the
/// account <b>name</b>, which really does encode size, stage, and status.
/// </summary>
/// <remarks>
/// The stage is a <i>fact about the account</i>. What that stage <i>means economically</i> is a separate
/// question the firm answers, not the venue — see <see cref="FirmConventions"/>.
/// </remarks>
public enum AccountStage
{
    /// <summary>
    /// The stage could not be identified. Deliberately the zero value, so an uninitialised stage is "unknown"
    /// rather than something tradeable.
    /// </summary>
    Unknown = 0,

    /// <summary>A practice account — no programme attached.</summary>
    Practice = 1,

    /// <summary>An evaluation / combine account: a paid attempt that passes or fails.</summary>
    Evaluation = 2,

    /// <summary>A funded account. Executes on the firm's platform, but a real payout rides on it.</summary>
    Funded = 3,
}
