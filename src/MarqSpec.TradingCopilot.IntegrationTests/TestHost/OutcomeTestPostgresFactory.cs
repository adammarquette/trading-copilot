using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the journal-outcome suites (gh#940, gh#908): the writer's own <c>OutcomeJournalHost</c> polls
/// every five minutes by default, which is outside any single test's window, but every other always-on
/// <see cref="IHostedService"/> (the venue monitor, the quote-driven promotion / conditional-firing / drift
/// consumers) has no such guarantee and could race a suite that deliberately seeds closed trades and untaken
/// suggestions to drive <c>OutcomeJournalService</c> by hand. Stripped for the same reason
/// <see cref="SuggestionGuardsTestPostgresFactory"/> strips them: these suites want the ONLY writer touching the
/// journal to be the call each test makes, so a race is never the explanation for a red or a green. Inherits the
/// adversarial venue stub from <see cref="StubbedVenuePostgresFactory"/> so the host still boots without reaching
/// a real broker.
/// </summary>
public sealed class OutcomeTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList())
        {
            services.Remove(hosted);
        }
    }
}
