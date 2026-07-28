using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the dead-man's-switch check-in suite (gh#408 ⇒ gh#244): the adversarial venue stub, plus two
/// test-only intercepts. (1) It <b>strips every always-on <see cref="IHostedService"/></b> — above all
/// <c>DeadMansSwitchHost</c>, which runs on the real wall clock and would fire its own passes underneath the
/// assertions; the suite drives <c>FlattenCheckInService.RunPassAsync(now, …)</c> at a chosen instant instead, the
/// sole pass. (2) It swaps the outbound <see cref="IDeadMansSwitch"/> for a
/// <see cref="RecordingDeadMansSwitch"/> — the external monitor cannot exist pre-merge, so the seam records what
/// this process vouched for and can refuse a report.
/// </summary>
/// <remarks>
/// The production registration is <c>AddHttpClient&lt;IDeadMansSwitch, HttpDeadMansSwitch&gt;</c> — a typed client
/// with <b>no decorator</b> in front of it, so replacing the interface here displaces nothing but the HTTP
/// transport (the gh#382 harness-bypass trap does not apply). The service under test, its schedules, and its
/// decision logic are the real ones.
/// </remarks>
public sealed class DeadMansSwitchTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The recording monitor this host's check-ins are reported to.</summary>
    public RecordingDeadMansSwitch Monitor { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host: the only check-in pass is the test's explicit RunPassAsync.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        services.RemoveAll<IDeadMansSwitch>();
        services.AddSingleton<IDeadMansSwitch>(Monitor);
    }
}
