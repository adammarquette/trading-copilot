using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
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
    public void AddTradingCopilotAi_ShouldBindTheStubProvider_WhenNoApiKeyIsConfigured()
    {
        using ServiceProvider provider = Build(apiKey: null);

        provider.GetRequiredService<ILlmProvider>().Should().BeOfType<StubLlmProvider>(
            "an unconfigured deployment must never fabricate a suggestion -- the stub only suppresses");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldBindTheAnthropicProvider_WhenAnApiKeyIsConfigured()
    {
        using ServiceProvider provider = Build(apiKey: "sk-test-key");

        provider.GetRequiredService<ILlmProvider>().Should().BeOfType<AnthropicLlmProvider>(
            "a configured deployment wakes the real model through the same seam");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldBindTheUnavailableReranker_WhenNoCohereKeyIsConfigured()
    {
        // The rerank seam (gh#975) selects the SAME way as the embed provider and the LLM provider — by the presence
        // of a key — so an unconfigured deployment gets the keyless passthrough default, never a missing dependency.
        using ServiceProvider provider = Build(apiKey: null, cohereApiKey: null);

        provider.GetRequiredService<IReranker>().Should().BeOfType<UnavailableReranker>(
            "an unconfigured deployment must get the passthrough reranker, never a missing dependency");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldBindTheCohereReranker_WhenACohereKeyIsConfigured()
    {
        using ServiceProvider provider = Build(apiKey: null, cohereApiKey: "cohere-test-key");

        provider.GetRequiredService<IReranker>().Should().BeOfType<CohereRerankProvider>(
            "a configured Cohere key wakes the real reranker through the same seam");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldRegisterTheLedgerNoLongerLivedThanItsDbContextOptions()
    {
        // The ledger injects the scoped DbContextOptions<TradingCopilotDbContext>. If it out-lives those options
        // (e.g. Singleton while the options are Scoped) it is a CAPTIVE DEPENDENCY: WebApplication.CreateBuilder
        // turns ValidateScopes / ValidateOnBuild ON in Development, so builder.Build() throws and the API host --
        // with the safety-critical trigger scan, auto-flatten, and kill switch it hosts -- never starts. The ledger
        // must therefore share its options' lifetime, exactly as every other DbContextOptions consumer (the trigger
        // scan, OCO-exit, ...) does. Asserted against the options' ACTUAL lifetime, so it stays correct even if that
        // registration ever changes.
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotData("Host=localhost;Database=x;Username=u;Password=p");
        services.AddTradingCopilotAi(new ConfigurationBuilder().Build());

        ServiceLifetime optionsLifetime = services
            .Single(descriptor => descriptor.ServiceType == typeof(DbContextOptions<TradingCopilotDbContext>)).Lifetime;
        ServiceLifetime ledgerLifetime = services
            .Single(descriptor => descriptor.ServiceType == typeof(IAiUsageLedger)).Lifetime;

        ledgerLifetime.Should().Be(
            optionsLifetime,
            "a ledger out-living the scoped DbContextOptions it injects is a captive dependency that fails the host's "
            + "scope validation at startup");
    }

    [Fact]
    public void AddTradingCopilotAi_ShouldRegisterTheEnrichmentSourceNoLongerLivedThanItsDbContext()
    {
        // The deep-tier enrichment source injects the scoped TradingCopilotDbContext (gh#476). A Singleton capturing it
        // would be a CAPTIVE DEPENDENCY failing ValidateScopes / ValidateOnBuild in Development -- the very trap the
        // ledger registration hit (gh#431). It must share the context's lifetime; asserted against the context's ACTUAL
        // lifetime so it stays correct even if that registration changes.
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotData("Host=localhost;Database=x;Username=u;Password=p");
        services.AddTradingCopilotAi(new ConfigurationBuilder().Build());

        ServiceLifetime contextLifetime = services
            .Single(descriptor => descriptor.ServiceType == typeof(TradingCopilotDbContext)).Lifetime;
        ServiceLifetime enricherLifetime = services
            .Single(descriptor => descriptor.ServiceType == typeof(IReviewEnrichmentSource)).Lifetime;

        enricherLifetime.Should().Be(
            contextLifetime,
            "an enricher out-living the scoped DbContext it injects is a captive dependency that fails scope validation "
            + "at startup");
    }

    /// <summary>
    /// The <c>generate_suggestion</c> write tool (gh#1134) must actually be <b>in the offered set</b>. A tool that
    /// compiles, is unit-tested, and is never registered is a feature that silently does not exist —
    /// <c>ChatTurnService</c> resolves <c>IEnumerable&lt;IChatTool&gt;</c>, so the registration <i>is</i> the wiring.
    /// Its lifetime is asserted against the <b>actual</b> lifetime of the <c>DbContextOptions</c> it injects: a
    /// longer-lived registration would be a captive dependency failing the host's <c>ValidateScopes</c> at startup —
    /// the API host, with the safety-critical scan / auto-flatten / kill switch it hosts, would never boot.
    /// </summary>
    [Fact]
    public void AddTradingCopilotAi_ShouldOfferTheGenerateSuggestionToolNoLongerLivedThanItsDbContextOptions()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotData("Host=localhost;Database=x;Username=u;Password=p");
        services.AddTradingCopilotAi(new ConfigurationBuilder().Build());

        ServiceDescriptor tool = services
            .Where(descriptor => descriptor.ServiceType == typeof(IChatTool))
            .Should().ContainSingle(descriptor => descriptor.ImplementationType == typeof(GenerateSuggestionTool),
                "the chat turn offers exactly the registered IChatTool set, so an unregistered tool is unreachable")
            .Which;

        ServiceLifetime optionsLifetime = services
            .Single(descriptor => descriptor.ServiceType == typeof(DbContextOptions<TradingCopilotDbContext>)).Lifetime;

        tool.Lifetime.Should().Be(
            optionsLifetime,
            "a tool out-living the scoped DbContextOptions it injects is a captive dependency that fails scope "
            + "validation at startup");
    }

    private static ServiceProvider Build(string? apiKey, string? cohereApiKey = null)
    {
        Dictionary<string, string?> settings = new();
        if (apiKey is not null)
        {
            settings["Llm:ApiKey"] = apiKey;
        }

        if (cohereApiKey is not null)
        {
            settings["Cohere:ApiKey"] = cohereApiKey;
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotAi(config);
        return services.BuildServiceProvider();
    }
}
