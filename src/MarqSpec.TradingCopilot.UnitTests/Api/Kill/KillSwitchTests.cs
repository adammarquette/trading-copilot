using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.UnitTests.Api.Observability;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Kill;

/// <summary>
/// The runtime kill-switch mirror (gh#189) and its execution-SLI gauge (gh#295): a killed system must be visible on
/// a dashboard, not only in a log, and the gauge is set from the one chokepoint every state change flows through.
/// </summary>
public class KillSwitchTests
{
    [Fact]
    public void Engage_ShouldSetTheEngagedGaugeToOne()
    {
        using MetricCapture capture = new();
        KillSwitch sut = new(capture.Metrics);

        sut.Engage(KillSwitchMode.FlattenAll, DateTimeOffset.UnixEpoch, "operator halt");

        capture.PumpGauges();
        capture.For(ExecutionMetrics.KillSwitchEngaged).Last().Value.Should().Be(1);
    }

    [Fact]
    public void Disengage_ShouldSetTheEngagedGaugeToZero()
    {
        using MetricCapture capture = new();
        KillSwitch sut = new(capture.Metrics);
        sut.Engage(KillSwitchMode.FlattenAll, DateTimeOffset.UnixEpoch, "operator halt");

        sut.Disengage();

        capture.PumpGauges();
        capture.For(ExecutionMetrics.KillSwitchEngaged).Last().Value.Should().Be(0);
    }
}
