namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>The lifecycle state of a <see cref="User"/>.</summary>
public enum UserStatus
{
    /// <summary>An active operator who can sign in and trade.</summary>
    Active = 0,

    /// <summary>A user whose access has been suspended.</summary>
    Suspended = 1,
}
