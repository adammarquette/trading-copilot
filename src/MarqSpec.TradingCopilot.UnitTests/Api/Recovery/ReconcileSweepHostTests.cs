using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The reconcile sweep's per-pass alert/meter fan-out (gh#722): <c>DetectAndAlert</c> runs the pure decision against
/// the register and, for each newly-alertable strand, raises the loud operator alert and increments the strand-
/// detected metric — then swaps the register. The behaviours that matter — a strand is metered <b>once</b> as it
/// ages, never re-metered while it stays stranded, re-metered only when it reappears fresh, and tagged by kind — are
/// asserted here; the EF discovery and the real alert sink are the QA integration tier's (the relational-only wall).
/// </summary>
public class ReconcileSweepHostTests
{
    private readonly IExecutionMetrics _metrics = A.Fake<IExecutionMetrics>();
    private readonly ReconcileStrandRegister _register = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly TimeSpan _bound = TimeSpan.FromMinutes(2);
    private readonly DateTimeOffset _t0 = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    private ReconcileStrandKey OrderStrand(Guid id) => new(ReconcileStrandKind.OrderTaking, _owner, id);

    private ReconcileStrandKey ConditionalStrand(Guid id) => new(ReconcileStrandKind.ConditionalFiring, _owner, id);

    private int DetectAndAlert(IReadOnlyList<ReconcileStrandKey> stranded, DateTimeOffset now) =>
        ReconcileSweepHost.DetectAndAlert(stranded, _register, _metrics, NullLogger.Instance, now, _bound);

    [Fact]
    public void DetectAndAlert_ShouldNotAlertOrMeter_WhenAStrandIsFirstSeen()
    {
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());

        int alerted = DetectAndAlert([strand], _t0);

        alerted.Should().Be(0);
        A.CallTo(() => _metrics.RecordReconcileStrandDetected(A<string>._)).MustNotHaveHappened();
        _register.Snapshot().Should().ContainKey(strand); // remembered, so it can age
    }

    [Fact]
    public void DetectAndAlert_ShouldAlertAndMeterOnce_WhenAStrandAgesPastTheBound()
    {
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        DetectAndAlert([strand], _t0); // first seen, clocked

        int alerted = DetectAndAlert([strand], _t0 + _bound);

        alerted.Should().Be(1);
        A.CallTo(() => _metrics.RecordReconcileStrandDetected(ExecutionMetrics.ReconcileStrandOrderTaking))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void DetectAndAlert_ShouldNotReMeter_WhenAStrandStaysStrandedAcrossPasses()
    {
        // Idempotency at the wiring level: a maybe-live strand meters (and pages) exactly once while it persists.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        DetectAndAlert([strand], _t0);
        DetectAndAlert([strand], _t0 + _bound); // alerts + meters once

        int alerted = DetectAndAlert([strand], _t0 + _bound + TimeSpan.FromMinutes(5));

        alerted.Should().Be(0);
        A.CallTo(() => _metrics.RecordReconcileStrandDetected(A<string>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void DetectAndAlert_ShouldMeterAgain_WhenAResolvedStrandReappearsAndReAges()
    {
        // The "treated fresh" property, end to end: a resolved-then-returning strand ages again and meters a SECOND
        // time -- the sweep neither forgets a genuinely re-stranded intent nor re-pages a still-open one.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        DetectAndAlert([strand], _t0);
        DetectAndAlert([strand], _t0 + _bound); // meters once
        DetectAndAlert([], _t0 + _bound + TimeSpan.FromMinutes(1)); // resolves -> forgotten

        DateTimeOffset reappearsAt = _t0 + TimeSpan.FromMinutes(10);
        DetectAndAlert([strand], reappearsAt).Should().Be(0, "the reappearance is re-clocked, not still-alerted");
        int reAlerted = DetectAndAlert([strand], reappearsAt + _bound);

        reAlerted.Should().Be(1);
        A.CallTo(() => _metrics.RecordReconcileStrandDetected(ExecutionMetrics.ReconcileStrandOrderTaking))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public void DetectAndAlert_ShouldTagTheMeterByKind_ForAConditionalStrand()
    {
        ReconcileStrandKey conditional = ConditionalStrand(Guid.NewGuid());
        DetectAndAlert([conditional], _t0);

        DetectAndAlert([conditional], _t0 + _bound);

        A.CallTo(() => _metrics.RecordReconcileStrandDetected(ExecutionMetrics.ReconcileStrandConditionalFiring))
            .MustHaveHappenedOnceExactly();
    }
}
