using MarqSpec.TradingCopilot.Api.MarketData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the suggestion-drift consumer suite (gh#632, gh#546): unlike the flatten/orphan factories, it
/// <b>keeps every always-on hosted service running</b> — in particular the live <c>SuggestionDriftHost</c> and
/// <c>SuggestionExpiryHost</c> — because the consumer's fresh-scope/cursor/teardown lifecycle (the DoD's explicit
/// ask) and the sibling-writer interleaving with the expire sweep are both live-host questions. Adds log capture
/// (mirroring <see cref="RetentionConsumerTestPostgresFactory"/>) so the teardown case can assert the host neither
/// swallows nor crash-loops on shutdown, and inherits the adversarial venue stub from
/// <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
public sealed class SuggestionDriftTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The captured log stream — asserted for a clean (uncaught-exception-free) host teardown.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }
}

/// <summary>
/// The teardown case's host, and <b>only</b> the host under test (gh#632 review). The sibling factory above keeps
/// every always-on hosted service running, which is right for the end-to-end consume case but wrong for a
/// lifecycle assertion: a second <see cref="SuggestionDriftHost"/> owned by the class fixture would poll and commit
/// the <i>same</i> <c>suggestion-drift</c> cursor on the <i>same</i> database as the host being torn down, so
/// contention or a log line from the fixture's copy lands inside the exact window under test — failing it for a
/// reason unrelated to gh#169, or masking a real <c>ObjectDisposedException</c> regression behind other traffic.
/// </summary>
/// <remarks>
/// So the fixture host here runs <b>no</b> hosted services, and <see cref="CreateDriftOnlyHost"/> adds back exactly
/// one: the consumer whose shutdown is the subject. Nothing else is polling, nothing else can log, and the test
/// owns the only lifetime in play.
/// </remarks>
public sealed class SuggestionDriftTeardownTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The captured log stream — asserted for a clean (uncaught-exception-free) host teardown.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    /// <summary>
    /// A host running the drift consumer and nothing else, whose lifetime the caller owns. Disposing the returned
    /// factory stops exactly that host.
    /// </summary>
    public WebApplicationFactory<Program> CreateDriftOnlyHost() =>
        WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddHostedService<SuggestionDriftHost>()));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Strip every hosted service from the FIXTURE host. CreateDriftOnlyHost re-adds the one under test, so the
        // suite never has two drift consumers racing one cursor.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
