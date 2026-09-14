using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The pre-merge host for the gh#360 news-ingestion suite: the real API pipeline over a throwaway Postgres
/// container, with two adversarial <see cref="StubNewsSource"/> feeds registered at the <see cref="INewsSource"/>
/// seam in place of the (not-yet-built, gh#383) real providers.
/// </summary>
/// <remarks>
/// The production <c>NewsIngestionHost</c> is removed: it polls on a timer, and this suite drives
/// <c>NewsIngestionService.IngestAsync</c> directly so each pass is deterministic and cannot race the timer.
/// </remarks>
public sealed class NewsIngestionTestPostgresFactory : PostgresApiFactory
{
    /// <summary>The "Finnhub" stand-in feed.</summary>
    public StubNewsSource Finnhub { get; } = new("finnhub");

    /// <summary>The "Tiingo" stand-in feed.</summary>
    public StubNewsSource Tiingo { get; } = new("tiingo");

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Two sources at the seam. Registering the same instances the test holds lets a test reconfigure what a
        // feed returns between passes.
        services.AddSingleton<INewsSource>(Finnhub);
        services.AddSingleton<INewsSource>(Tiingo);

        // Drop the timer-driven host so the only ingestion is the one a test triggers.
        foreach (ServiceDescriptor hosted in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                     .ToList())
        {
            services.Remove(hosted);
        }
    }
}
