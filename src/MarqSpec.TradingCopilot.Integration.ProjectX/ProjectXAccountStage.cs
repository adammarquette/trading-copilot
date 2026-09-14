using System.Text.RegularExpressions;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// Resolves a ProjectX account's <see cref="AccountStage"/> from its <b>name</b> — where ProjectX encodes size,
/// stage and status (e.g. <c>PRAC-50K-1234</c>, <c>50KTC-V2-DLL-0000</c>).
/// </summary>
/// <remarks>
/// <para>
/// The patterns are <b>grounded in a real Topstep roster</b> (gh#76: 294 accounts, six name families, zero
/// unmatched — wiki: <c>projectx-gateway-api.md</c> "Account name families"): <c>PRAC</c> /
/// <c>PRACTICE&lt;mon&gt;&lt;digits&gt;</c> → Practice, <c>&lt;size&gt;KTC</c> (Trading Combine) /
/// <c>S&lt;step&gt;&lt;mon&gt;&lt;digits&gt;</c> (legacy step) → Evaluation, and <c>EXPRESS…</c> (Express
/// Funded Account) → Funded. The patterns are anchored, not fuzzy: near-misses stay
/// <see cref="AccountStage.Unknown"/>, because a wrong guess is exactly the failure gh#60 removed — a misread
/// funded account classified as practice would be traded with real money at stake. Misreading <i>as</i> funded
/// is the over-cautious direction; nothing here can promote an account into a <i>less</i> guarded stage than a
/// declaration would.
/// </para>
/// <para>
/// <see cref="AccountStage.Unknown"/> remains the answer for everything else — it resolves to
/// <see cref="TradingMode.Undeclared"/> and is tradeable nowhere until the operator classifies it via the
/// per-account override (gh#76). <b>Grounded against Topstep only:</b> another ProjectX-family firm may reuse
/// these spellings with different meanings — revisit this table (and the wiki page) when onboarding one; the
/// override is the correction path in the meantime.
/// </para>
/// </remarks>
public static partial class ProjectXAccountStage
{
    private const string Months = "JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC";

    /// <summary>The modern Trading Combine marker — the first segment, e.g. <c>50KTC</c> / <c>150KTC</c>.</summary>
    [GeneratedRegex(@"^\d+KTC$", RegexOptions.IgnoreCase)]
    private static partial Regex CombineMarker();

    /// <summary>A legacy single-segment step-evaluation name, e.g. <c>S1JUL1015039217</c>.</summary>
    [GeneratedRegex($@"^S\d(?:{Months})\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyStepName();

    /// <summary>A legacy single-segment practice name, e.g. <c>PRACTICEJUL214923016</c>.</summary>
    [GeneratedRegex($@"^PRACTICE(?:{Months})\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyPracticeName();

    /// <summary>A legacy single-segment funded name, e.g. <c>EXPRESSJUL1015027963</c>.</summary>
    [GeneratedRegex($@"^EXPRESS(?:{Months})\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyFundedName();

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

        // The first hyphen-delimited segment carries the type marker in the modern families. Matching the whole
        // segment -- not a prefix -- keeps "PRACTICE-…", or a name that merely contains a marker, from matching.
        string marker = accountName.Split('-', 2)[0];

        if (marker.Equals("PRAC", StringComparison.OrdinalIgnoreCase))
        {
            return AccountStage.Practice;
        }

        if (CombineMarker().IsMatch(marker))
        {
            return AccountStage.Evaluation;
        }

        if (marker.Equals("EXPRESS", StringComparison.OrdinalIgnoreCase) && marker.Length != accountName.Length)
        {
            // Hyphenated Express Funded Account (EXPRESS-V2-…). A name that is JUST "EXPRESS" with no further
            // segments matches no observed family and stays Unknown.
            return AccountStage.Funded;
        }

        // The legacy families are single-segment whole names (marker + month + digits, no hyphens).
        if (LegacyPracticeName().IsMatch(accountName))
        {
            return AccountStage.Practice;
        }

        if (LegacyStepName().IsMatch(accountName))
        {
            return AccountStage.Evaluation;
        }

        if (LegacyFundedName().IsMatch(accountName))
        {
            return AccountStage.Funded;
        }

        return AccountStage.Unknown;
    }
}
