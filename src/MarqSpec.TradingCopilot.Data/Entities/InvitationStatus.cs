namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>The lifecycle state of an <see cref="Invitation"/>.</summary>
public enum InvitationStatus
{
    /// <summary>Issued and not yet used — can be accepted while unexpired.</summary>
    Pending = 0,

    /// <summary>Accepted — a user account was created from it (single-use).</summary>
    Accepted = 1,

    /// <summary>Revoked by the operator before use.</summary>
    Revoked = 2,

    /// <summary>Expired without being accepted.</summary>
    Expired = 3,
}
