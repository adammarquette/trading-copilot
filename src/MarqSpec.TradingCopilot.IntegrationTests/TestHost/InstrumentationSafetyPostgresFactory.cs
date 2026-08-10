using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the instrumentation-safety cases (gh#331): observability must never be able to break trading
/// (engineering §9). It runs the whole suite under a <b>configured but unreachable</b> OTLP endpoint — the realistic
/// bad day, harder than the "no exporter configured" case ADR-0002 already records as verified — and swaps the
/// execution-metrics sink for one that can be made to <b>throw on a chosen measurement</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fault is injected INSIDE the shipped decorator, not in place of it (gh#382).</b> The composition root binds
/// <see cref="IExecutionMetrics"/> to <see cref="FailureTolerantExecutionMetrics"/> wrapping the real sink (gh#343);
/// a harness that simply re-registers <see cref="IExecutionMetrics"/> with its own double <b>removes that guard from
/// the host</b>, so the suite measures a system the deployment does not have and its pins pass whatever production
/// does. This factory therefore reproduces the production chain with the fault injector as the <i>inner</i> sink —
/// the object under test is the <b>shipped</b> decorator. ADR-0002's gh#343 Update records the same trap.
/// </para>
/// <para>
/// Reproducing the chain is still an assumption about the composition root, so <see cref="DisplacedRegistration"/>
/// exposes the descriptor this factory replaced and
/// <c>InstrumentationSafetyIntegrationTests.Harness_ShouldWrapTheFaultInjector_InTheSameDecoratorProductionRegisters</c>
/// asserts it really was the decorator. If production stops decorating, or wraps something further, that guard fails
/// rather than the suite going quietly vacuous a second time.
/// </para>
/// </remarks>
public sealed class InstrumentationSafetyPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The sink under the system, with a switch per measurement — set a flag to make that call throw.</summary>
    public FaultInjectingExecutionMetrics Metrics { get; } = new();

    /// <summary>
    /// The <see cref="IExecutionMetrics"/> registration this factory displaced — i.e. what the composition root binds
    /// in production. Kept so a test can resolve it and assert the shape this harness reproduces (gh#382).
    /// </summary>
    public ServiceDescriptor? DisplacedRegistration { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Nothing listens on port 1. The SDK is fully wired and every export attempt fails — the state a deployment
        // is in whenever the collector is down, restarting, or misaddressed.
        builder.UseSetting("Telemetry:OtlpEndpoint", "http://127.0.0.1:1");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        ServiceDescriptor? sink = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IExecutionMetrics));
        if (sink is not null)
        {
            DisplacedRegistration = sink;
            services.Remove(sink);
        }

        // The fault injector goes UNDER the production decorator, never in place of it: arming a fault must exercise
        // FailureTolerantExecutionMetrics, which is the thing gh#331 found missing and gh#343 shipped.
        services.AddSingleton<IExecutionMetrics>(provider => new FailureTolerantExecutionMetrics(
            Metrics,
            provider.GetRequiredService<ILogger<FailureTolerantExecutionMetrics>>()));
    }
}

/// <summary>
/// An <see cref="IExecutionMetrics"/> that records nothing and throws on whichever measurement a test arms. The
/// sanctioned way to ask "can instrumentation break trading?" at the seam the system actually depends on, rather
/// than by simulating a fault somewhere it could not really occur (gh#331).
/// </summary>
public sealed class FaultInjectingExecutionMetrics : IExecutionMetrics
{
    /// <summary>Makes <see cref="RecordOrderAck"/> throw — the measurement taken <b>after</b> the venue accepted an order.</summary>
    public bool ThrowOnOrderAck { get; set; }

    /// <summary>Makes <see cref="RecordGateDecision"/> throw — the measurement taken <b>before</b> the venue is reached.</summary>
    public bool ThrowOnGateDecision { get; set; }

    /// <summary>Makes <see cref="SetKillSwitchEngaged"/> throw — the operator's emergency halt.</summary>
    public bool ThrowOnKillSwitch { get; set; }

    /// <summary>Clears every armed fault — call at the start of each test.</summary>
    public void Reset()
    {
        ThrowOnOrderAck = false;
        ThrowOnGateDecision = false;
        ThrowOnKillSwitch = false;
    }

    /// <inheritdoc />
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer) => FailIf(ThrowOnGateDecision);

    /// <inheritdoc />
    public void RecordFlattenDeadline(FlattenTier tier, string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void RecordOrderAck(TimeSpan elapsed) => FailIf(ThrowOnOrderAck);

    /// <inheritdoc />
    public void SetKillSwitchEngaged(bool engaged) => FailIf(ThrowOnKillSwitch);

    /// <inheritdoc />
    public void SetOrphanedStops(int count)
    {
    }

    /// <inheritdoc />
    public void SetPositionProtection(int open, int unprotected)
    {
    }

    /// <inheritdoc />
    public void SetVenueConnected(bool connected)
    {
    }

    /// <inheritdoc />
    public void RecordRetentionGap(string consumerGroup)
    {
    }

    /// <inheritdoc />
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag)
    {
    }

    /// <inheritdoc />
    public void RecordBackfillShortfall(string contractKey, TimeSpan uncovered)
    {
    }

    /// <inheritdoc />
    public void RecordTradeJournalOutcome(string outcome)
    {
    }

    private static void FailIf(bool armed)
    {
        if (armed)
        {
            throw new InvalidOperationException("Metrics sink failed (test double) — this must never reach a trading action.");
        }
    }
}
