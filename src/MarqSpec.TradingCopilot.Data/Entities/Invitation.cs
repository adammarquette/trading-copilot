namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A dormant onboarding invitation (R-18, ADR-0017 §4) — built for the multi-user era and deliberately retained:
/// not part of the product's onboarding story (the operator is seeded at deploy) but the plumbing a future
/// read-only / mentee login would reuse. The operator issues it (email-bound, single-use, expiring); the invitee
/// accepts it — with the invite token + a password — to create their <see cref="User"/> account. There is no open
/// sign-up. Not user-owned: it is looked up anonymously by token hash during accept.
/// </summary>
public class Invitation
{
    /// <summary>The invitation's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The invited email — the account is created for this address.</summary>
    public required string Email { get; set; }

    /// <summary>The hash of the invite token. The plaintext token is shown once and never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The user who issued the invitation.</summary>
    public Guid IssuedByUserId { get; set; }

    /// <summary>The invitation's lifecycle state.</summary>
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    /// <summary>When the invitation was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the invitation expires (UTC).</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>The user created on acceptance, if accepted.</summary>
    public Guid? AcceptedByUserId { get; set; }

    /// <summary>Whether this invitation can be accepted at <paramref name="now"/> (pending and unexpired).</summary>
    /// <param name="now">The current instant.</param>
    /// <returns><see langword="true"/> if it is still acceptable.</returns>
    public bool CanBeAcceptedAt(DateTimeOffset now)
    {
        return Status == InvitationStatus.Pending && now < ExpiresUtc;
    }
}
