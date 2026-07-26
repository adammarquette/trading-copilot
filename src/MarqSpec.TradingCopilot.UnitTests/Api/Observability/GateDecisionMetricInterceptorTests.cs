using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// The gate-decisions SLI (gh#295): counting every <see cref="GateDecisionRecord"/> as it is written makes the
/// counter reconcile 1:1 with the rows by construction — PRD §7's "100% risk-gate coverage", observable.
/// </summary>
public class GateDecisionMetricInterceptorTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext ContextWith(GateDecisionMetricInterceptor interceptor) =>
        new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options,
            new FixedUser(Guid.NewGuid()));

    private static GateDecisionRecord NewDecision(GateOutcome outcome, RiskLayer? bindingLayer) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Outcome = outcome,
        ApprovedQuantity = outcome == GateOutcome.Blocked ? 0 : 1,
        BindingLayer = bindingLayer,
        Reason = "test",
        DecidedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task SavingChanges_ShouldCountAGateDecision_ByOutcomeAndBindingLayer()
    {
        using MetricCapture capture = new();
        GateDecisionMetricInterceptor interceptor = new(capture.Metrics, NullLogger<GateDecisionMetricInterceptor>.Instance);
        await using TradingCopilotDbContext db = ContextWith(interceptor);
        db.GateDecisions.Add(NewDecision(GateOutcome.Allowed, bindingLayer: null));

        await db.SaveChangesAsync();

        IReadOnlyList<(string Instrument, double Value, Dictionary<string, string?> Tags)> recorded =
            capture.For(ExecutionMetrics.GateDecisions);
        recorded.Should().ContainSingle();
        recorded[0].Tags["outcome"].Should().Be(nameof(GateOutcome.Allowed));
        recorded[0].Tags["binding_layer"].Should().Be("none", "an always-present tag keeps the series shape stable");
    }

    [Fact]
    public async Task SavingChanges_ShouldCountEveryRow_SoTheCounterReconcilesWithThePersistedRows()
    {
        using MetricCapture capture = new();
        GateDecisionMetricInterceptor interceptor = new(capture.Metrics, NullLogger<GateDecisionMetricInterceptor>.Instance);
        await using TradingCopilotDbContext db = ContextWith(interceptor);
        db.GateDecisions.Add(NewDecision(GateOutcome.Allowed, bindingLayer: null));
        db.GateDecisions.Add(NewDecision(GateOutcome.Resized, bindingLayer: null));
        db.GateDecisions.Add(NewDecision(GateOutcome.Blocked, bindingLayer: null));

        await db.SaveChangesAsync();

        capture.For(ExecutionMetrics.GateDecisions).Should().HaveCount(3, "one measurement per persisted row");
    }
}
