using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// Subclass of <see cref="PostgresApiFactory"/> equipping integration tests with the adversarial venue stub
/// and configurable deployment environment settings.
/// </summary>
public class StubbedVenuePostgresFactory : PostgresApiFactory
{
    public WebApplicationFactory<Program> CreateHost(DeploymentEnvironment environment)
    {
        return WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment.ToString());
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ProjectX:CredentialKey", "topstep-main");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        ServiceDescriptor? factoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IProjectXVenueFactory));
        if (factoryDescriptor is not null)
        {
            services.Remove(factoryDescriptor);
        }

        services.AddScoped<IProjectXVenueFactory, AdversarialTestProjectXVenueFactory>();
    }
}
