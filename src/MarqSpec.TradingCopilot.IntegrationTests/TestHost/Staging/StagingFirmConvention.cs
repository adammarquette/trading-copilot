using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// The single declared R-14 classification this harness's reserved account trades under — referenced by
/// <b>both</b> the app-side connection <see cref="StagingVenueExecutionSuite.EnsureConnectionAsync"/> declares
/// over HTTP (<c>PUT /firms/{id}/conventions</c>) and the gateway-side
/// <see cref="StagingProjectXGateway.ReservedAccountConventions"/> guard that governs the gateway's own direct
/// writes (gh#1074). One named source rather than two textually-identical literals, so the two paths cannot
/// drift into declaring different things for the same account.
/// </summary>
internal static class StagingFirmConvention
{
    /// <summary>The firm name every staging suite creates its connection under.</summary>
    public const string FirmName = "Staging-Execution-Gate";

    /// <summary>The only stage this firm declares — the reserved account's classified name resolves to this.</summary>
    public const AccountStage ReservedStage = AccountStage.Practice;

    /// <summary>Whether capital is at risk at <see cref="ReservedStage"/> — never, for the reserved account.</summary>
    public const bool ReservedCapitalAtRisk = false;
}
