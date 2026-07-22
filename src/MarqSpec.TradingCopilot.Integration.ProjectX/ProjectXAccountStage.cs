using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// Resolves a ProjectX account's <see cref="AccountStage"/> from its <b>name</b> — where ProjectX encodes size,
/// stage and status (e.g. <c>PRAC-50K-1234</c>, <c>50KTC-V2-DLL-0000</c>).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>conservative</b>: it recognises only the <c>PRAC</c> practice marker — which prop firms use
/// distinctly — and returns <see cref="AccountStage.Unknown"/> for everything else. Funded and evaluation naming
/// varies by firm and is not reliably documented, and a wrong guess is exactly the failure gh#60 removed: a
/// misread funded account classified as practice would be traded with real money at stake.
/// </para>
/// <para>
/// <see cref="AccountStage.Unknown"/> is the safe answer — it resolves to <see cref="TradingMode.Undeclared"/>
/// and is tradeable nowhere until the operator classifies it. Firm-specific funded/evaluation patterns and a
/// per-account operator override are deferred to a later increment that grounds them in a real account roster
/// (gh#76), rather than guessed here.
/// </para>
/// </remarks>
public static class ProjectXAccountStage
{
    /// <summary>Resolves the stage from a ProjectX account name.</summary>
    /// <param name="accountName">The gateway's account name.</param>
    /// <returns>
    /// The stage, or <see cref="AccountStage.Unknown"/> when the name is absent or not confidently recognised.
    /// </returns>
    public static AccountStage Resolve(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return AccountStage.Unknown;
        }

        // The first hyphen-delimited segment carries the type marker. Matching the whole segment -- not a prefix
        // -- keeps "PRACTICE-…", or a funded name that merely contains "prac", from being read as practice.
        string marker = accountName.Split('-', 2)[0];

        return marker.Equals("PRAC", StringComparison.OrdinalIgnoreCase)
            ? AccountStage.Practice
            : AccountStage.Unknown;
    }
}
