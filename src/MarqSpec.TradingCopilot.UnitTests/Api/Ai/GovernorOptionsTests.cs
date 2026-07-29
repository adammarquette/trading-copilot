using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The platform-spend governor's configuration (gh#448, ADR-0008): static Options-pattern config, opt-in and INERT
/// until a positive daily budget is set. <see cref="GovernorOptions.ToBudget"/> is the seam the scan reads once per
/// pass — null (no cap) unless a positive budget is present, else the projected domain <see cref="AiSpendBudget"/>.
/// The DI tests exercise the REAL construction path (AGENT-MEMORY: exercise the production DI, not a hand-built
/// options object): the <c>.Validate(...).ValidateOnStart()</c> registered by <c>AddTradingCopilotAi</c> must reject
/// a non-positive budget and an out-of-range fraction, bind cleanly (null budget) when the section is absent, and
/// resolve the concrete governor.
/// </summary>
public class GovernorOptionsTests
{
    [Fact]
    public void ToBudget_ShouldBeNull_AndInactive_WhenDailyBudgetIsNull()
    {
        GovernorOptions options = new(); // DailyBudgetUsd absent (null)

        options.IsActive.Should().BeFalse();
        options.ToBudget().Should().BeNull();
    }

    [Fact]
    public void ToBudget_ShouldBeNull_AndInactive_WhenDailyBudgetIsZero()
    {
        GovernorOptions options = new() { DailyBudgetUsd = 0m };

        options.IsActive.Should().BeFalse();
        options.ToBudget().Should().BeNull();
    }

    [Fact]
    public void ToBudget_ShouldBeNull_AndInactive_WhenDailyBudgetIsNegative()
    {
        GovernorOptions options = new() { DailyBudgetUsd = -5m };

        options.IsActive.Should().BeFalse();
        options.ToBudget().Should().BeNull();
    }

    [Fact]
    public void ToBudget_ShouldProjectTheBudgetAndThreshold_WhenDailyBudgetIsPositive()
    {
        GovernorOptions options = new() { DailyBudgetUsd = 10m, AlertThresholdFraction = 0.8m };

        options.IsActive.Should().BeTrue();
        AiSpendBudget? budget = options.ToBudget();
        budget.Should().NotBeNull();
        budget!.DailyBudgetUsd.Should().Be(10m);
        budget.AlertThresholdFraction.Should().Be(0.8m);
        budget.ThresholdUsd.Should().Be(8m);
    }

    // (a) A configured non-positive budget must fail validation -- surfaced on the first .Value access (the same
    // check ValidateOnStart forces eagerly under a real host).
    [Fact]
    public void AddTradingCopilotAi_ShouldFailValidation_WhenDailyBudgetIsNegative()
    {
        using ServiceProvider provider = Build(new() { ["Governor:DailyBudgetUsd"] = "-5" });

        Action resolve = () => _ = provider.GetRequiredService<IOptions<GovernorOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    // (b) A fraction outside (0, 1] must fail validation.
    [Fact]
    public void AddTradingCopilotAi_ShouldFailValidation_WhenAlertThresholdFractionExceedsOne()
    {
        using ServiceProvider provider = Build(new() { ["Governor:AlertThresholdFraction"] = "1.5" });

        Action resolve = () => _ = provider.GetRequiredService<IOptions<GovernorOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    // (c) An absent Governor section binds cleanly to the inert default -- a fresh deploy keeps the no-cap status quo.
    [Fact]
    public void AddTradingCopilotAi_ShouldBindInert_WhenTheGovernorSectionIsAbsent()
    {
        using ServiceProvider provider = Build(new());

        GovernorOptions options = provider.GetRequiredService<IOptions<GovernorOptions>>().Value;
        options.DailyBudgetUsd.Should().BeNull();
        options.ToBudget().Should().BeNull();
    }

    // (d) The concrete pure gate is bound behind the interface.
    [Fact]
    public void AddTradingCopilotAi_ShouldResolveTheConcreteGovernor()
    {
        using ServiceProvider provider = Build(new());

        provider.GetRequiredService<IAiSpendGovernor>().Should().BeOfType<AiSpendGovernor>();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddTradingCopilotAi(config);
        return services.BuildServiceProvider();
    }
}
