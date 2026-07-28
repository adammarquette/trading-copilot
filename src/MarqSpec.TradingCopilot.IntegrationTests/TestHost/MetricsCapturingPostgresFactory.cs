using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the execution-SLI suite (gh#330): the adversarial venue + host-stripping, and it swaps the DI
/// <see cref="ExecutionMetrics"/> for one published under a <b>unique meter name</b>, so a <see cref="MetricsCapture"/>
/// listener observes <b>only this suite's</b> emissions and never a parallel test class racing on the shared
/// production meter — the exact isolation the <c>ExecutionMetrics(meterName)</c> ctor exists to provide.
/// </summary>
public sealed class MetricsCapturingPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter name this host's <see cref="ExecutionMetrics"/> publishes under; filter a capture to it.</summary>
    public string MeterName { get; } = $"{ExecutionMetrics.MeterName}.Test.{Guid.NewGuid():N}";

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic: no always-on hosted service emits on our meter behind the assertions.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        // One ExecutionMetrics on our own meter name, served for both the concrete type and the Domain seam.
        services.RemoveAll<ExecutionMetrics>();
        services.RemoveAll<IExecutionMetrics>();
        ExecutionMetrics metrics = new(MeterName);
        services.AddSingleton(metrics);
        services.AddSingleton<IExecutionMetrics>(metrics);
    }
}
