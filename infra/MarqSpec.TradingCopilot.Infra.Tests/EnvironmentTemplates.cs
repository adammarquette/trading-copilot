using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// The two environments, synthesised once per test class. The outbound shape used here is
/// arbitrary for the tests and is not a choice — every assertion in the classes that share
/// this fixture holds in all four shapes; the shapes themselves are asserted in
/// <see cref="OutboundPathTests"/>.
/// </summary>
public sealed class EnvironmentTemplates
{
    public const OutboundPath FixtureShape = OutboundPath.NatGateway;

    public Synthesised Production { get; } = Synthesised.Production(FixtureShape);

    public Synthesised Staging { get; } = Synthesised.Staging(FixtureShape);

    public static IEnumerable<object[]> Both =>
    [
        ["production"],
        ["staging"],
    ];

    public Synthesised For(string envName) => envName == "production" ? Production : Staging;
}
