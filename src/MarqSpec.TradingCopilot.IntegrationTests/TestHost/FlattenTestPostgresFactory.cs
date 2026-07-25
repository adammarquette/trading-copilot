using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the auto-flatten suites (gh#186 / gh#188): the adversarial venue stub, plus two test-only
/// intercepts. (1) It <b>strips every always-on <see cref="IHostedService"/></b> — the flatten scheduler,
/// watchdog, connection monitor, quote ingestion, and consumers all run on the real wall clock and would fire
/// nondeterministically once the stub reports open positions, contaminating close counts and <c>flatten.*</c>
/// events; the suite instead drives the service's public <c>RunPassAsync(now, …)</c> at a chosen instant, the sole
/// pass. (2) It declares <b>CL disabled</b> so the R-13 "disabled market with a live position stays visible, never
/// a silent no-op" path can be exercised while ES/NQ/GC keep their built-in deadlines.
/// </summary>
public sealed class FlattenTestPostgresFactory : StubbedVenuePostgresFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Flatten:Instruments:0:Symbol", "CL");
        builder.UseSetting("Flatten:Instruments:0:Enabled", "false");
        builder.UseSetting("Flatten:Instruments:0:SessionClose", "13:15");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host: the only flatten pass is the test's explicit RunPassAsync.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
