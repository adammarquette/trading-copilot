using Microsoft.AspNetCore.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The shared host booted as <b><c>Production</c></b>, so the R-14 environment gate on the interactive Scalar
/// console (<c>ScalarUiPolicy</c>, gh#604) is exercised at its live end rather than only as a pure function
/// (gh#612).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a whole second host.</b> <c>ScalarUiPolicyTests</c> (the unit tier, gh#604) proves
/// <c>IsEnabledFor(Production)</c> returns <see langword="false"/>. Nothing proves the composition root ever
/// <i>asks</i> it: a refactor that dropped the <c>if</c> in <c>MapTradingCopilotApiReference</c>, or that resolved
/// the environment from the wrong place, would leave every unit test green while shipping a one-click trigger for
/// <c>POST /accounts/{id}/orders</c> and <c>POST /kill-switch</c> — against the live, real-money venue — to
/// anyone who reaches the page. The gate only exists at the composition root, so only a real host can witness it.
/// </para>
/// <para>
/// <c>Program</c> reads <c>builder.Environment.EnvironmentName</c> exactly once, through
/// <c>DeploymentEnvironmentMapping.From</c>, and nothing else in the composition root branches on the
/// environment — so this override changes precisely the one thing under test and the host is otherwise the
/// shipped pipeline, against its own throwaway container like every other suite.
/// </para>
/// </remarks>
public sealed class ProductionDocsPostgresFactory : PostgresApiFactory
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}
