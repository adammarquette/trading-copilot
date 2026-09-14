using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the OCO-cancel-on-exit suite (gh#184). It <b>strips every always-on <see cref="IHostedService"/></b>
/// — most of all the <c>AccountEventStreamHost</c> — so nothing races the seeded surface. The OCO service
/// (<c>OcoExitService.ProcessExitAsync</c>) is a per-event service driven directly at a chosen
/// <c>PositionEvent</c>; the adversarial stub cannot push account events (no <c>AccountStreaming</c> capability),
/// so the live host would only sit idle. Inherits the adversarial venue stub from
/// <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
public sealed class OcoExitTestPostgresFactory : StubbedVenuePostgresFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
