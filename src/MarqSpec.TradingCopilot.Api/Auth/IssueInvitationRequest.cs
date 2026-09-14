namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>A request to issue an onboarding invitation.</summary>
/// <param name="Email">The email to invite.</param>
public sealed record IssueInvitationRequest(string Email);
