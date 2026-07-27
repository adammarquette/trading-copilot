using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A host with the OTLP exporter <b>enabled but pointed at an unreachable endpoint</b> (gh#331): the exact
/// "observability collector is down" condition, so a test can prove it never blocks trading. Export is genuinely on
/// (<c>TelemetryOptions.ExportEnabled</c> is true), and nothing listens on the endpoint — the SDK's batch export
/// processor runs off the request path, so a send / flatten / consume must still complete. Extends the adversarial
/// venue host and strips the always-on <see cref="IHostedService"/>s so nothing else races the assertion.
/// </summary>
public sealed class TelemetryDeadExporterPostgresFactory : StubbedVenuePostgresFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Refused by construction: nothing listens on 127.0.0.1:1. Export is wired and will fail to reach it.
        builder.UseSetting("Telemetry:OtlpEndpoint", "http://127.0.0.1:1");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
