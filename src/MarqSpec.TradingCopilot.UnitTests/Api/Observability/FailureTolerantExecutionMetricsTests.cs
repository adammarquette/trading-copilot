using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// The failure-tolerant metrics decorator (gh#343): observability must never be able to break trading
/// (engineering §9) — <i>"emitting a metric can never fail a trading action: a metrics failure is logged and the
/// action completes"</i>. Found by QA gh#331, where a throwing sink failed a send, left a live order unjournaled,
/// and failed the operator's emergency halt.
/// </summary>
public class FailureTolerantExecutionMetricsTests
{
    private readonly IExecutionMetrics _inner = A.Fake<IExecutionMetrics>();
    private readonly CapturingLogger _logger = new();

    private FailureTolerantExecutionMetrics Sut => new(_inner, _logger);

    /// <summary>Exercises every member, so a measurement added later cannot quietly escape the guard.</summary>
    private static void InvokeEveryMeasurement(IExecutionMetrics metrics)
    {
        metrics.RecordGateDecision(GateOutcome.Allowed, bindingLayer: null);
        metrics.RecordFlattenDeadline(FlattenTier.Primary, "executed");
        metrics.RecordTimeToFlat(FlattenTier.Primary, TimeSpan.FromSeconds(1));
        metrics.RecordOrderAck(TimeSpan.FromMilliseconds(50));
        metrics.SetKillSwitchEngaged(engaged: true);
        metrics.SetOrphanedStops(3);
        metrics.RecordRetentionGap("stop-promotion");
        metrics.RecordPipelineLag("stop-promotion", TimeSpan.FromMilliseconds(20));
        metrics.RecordBackfillShortfall("CON.F.US.MES.U26", TimeSpan.FromMinutes(40));
    }

    private void MakeInnerThrow()
    {
        A.CallTo(_inner).Throws(new InvalidOperationException("the sink is down"));
    }

    [Fact]
    public void EveryMeasurement_ShouldNotThrow_WhenTheInnerSinkThrows()
    {
        // THE point of the class. gh#331 observed the alternative: a throwing sink failed the send AFTER the venue
        // had accepted the order, leaving a live position with no Order row and no stop plan.
        MakeInnerThrow();

        Action record = () => InvokeEveryMeasurement(Sut);

        record.Should().NotThrow("a metrics fault must never reach the trading action that was being measured");
    }

    [Fact]
    public void EveryMeasurement_ShouldLogAnError_WhenTheInnerSinkThrows()
    {
        // Swallowed SILENTLY is the other way to get this wrong: a sink that has been failing for a week must be
        // discoverable, so each absorbed fault is logged at error.
        MakeInnerThrow();

        InvokeEveryMeasurement(Sut);

        _logger.Entries.Should().HaveCount(9, "every absorbed measurement fault is surfaced");
        _logger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void EveryMeasurement_ShouldReachTheInnerSink_WhenItIsHealthy()
    {
        // The decorator must not become a null sink: a healthy measurement still has to be recorded, or the SLIs
        // silently stop existing — the failure gh#232's absence-detection exists to make visible.
        InvokeEveryMeasurement(Sut);

        A.CallTo(() => _inner.RecordGateDecision(GateOutcome.Allowed, null)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordFlattenDeadline(FlattenTier.Primary, "executed")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordTimeToFlat(FlattenTier.Primary, TimeSpan.FromSeconds(1))).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordOrderAck(TimeSpan.FromMilliseconds(50))).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.SetKillSwitchEngaged(true)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.SetOrphanedStops(3)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordRetentionGap("stop-promotion")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordPipelineLag("stop-promotion", TimeSpan.FromMilliseconds(20)))
            .MustHaveHappenedOnceExactly();
        _logger.Entries.Should().BeEmpty("a healthy sink logs nothing");
    }

    private sealed class CapturingLogger : ILogger<FailureTolerantExecutionMetrics>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
