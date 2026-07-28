using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The AI composition (gh#402, ADR-0008): the reviewer is <b>always bound</b>, and which concrete it binds is a
/// property of the composition root (the presence of an <c>Llm:ApiKey</c>), not of anything the scan does. A
/// regression that dropped the switch would leave the route silently review-less with every other test still green —
/// same reasoning as the notification-registration guard.
/// </summary>
public class AiRegistrationTests
{
    [Fact]
    public void AddTradingCopilotAi_ShouldBindTheInertReviewer_WhenNoApiKeyIsConfigured()
    {
        using ServiceProvider provider = Build(apiKey: null);
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITriggerReviewer>().Should().BeOfType<NullTriggerReviewer>(
            "an unconfigured deployment must get the honest inert reviewer, never a missing dependency");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldBindTheLlmReviewer_WhenAnApiKeyIsConfigured()
    {
        using ServiceProvider provider = Build(apiKey: "sk-test-key");
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITriggerReviewer>().Should().BeOfType<LlmTriggerReviewer>();
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldAlwaysBindTheStubProvider_ThisIncrement()
    {
        using ServiceProvider provider = Build(apiKey: null);

        provider.GetRequiredService<ILlmProvider>().Should().BeOfType<StubLlmProvider>();
    }

    private static ServiceProvider Build(string? apiKey)
    {
        Dictionary<string, string?> settings = new();
        if (apiKey is not null)
        {
            settings["Llm:ApiKey"] = apiKey;
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotAi(config);
        return services.BuildServiceProvider();
    }
}
