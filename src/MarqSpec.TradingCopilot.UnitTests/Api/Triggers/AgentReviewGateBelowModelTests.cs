using System.Reflection;
using MarqSpec.TradingCopilot.Api.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// GATE-BELOW-MODEL (gh#402, AGENTS.md "enforcement lives below the model"): the agent-review path <b>proposes</b> —
/// it persists a Suggestion and at most an advisory, and NOTHING else. This asserts, structurally, that neither the
/// scan service nor the reviewer takes a constructor dependency that could reach execution: no order executor, no
/// risk gate, no venue / ProjectX client. A future edit that wired one in would fail here rather than in production.
/// </summary>
public class AgentReviewGateBelowModelTests
{
    // Fragments of type names that can place, size, route, or gate an order. The agent-review path must reach NONE.
    private static readonly string[] _forbiddenTypeFragments =
    [
        "IOrderExecutor",
        "OrderExecution",
        "IRiskGate",
        "ITradingVenue",
        "IVenueConnection",
        "IAccountEventStream",
        "ProjectX",
    ];

    [Theory]
    [InlineData(typeof(TriggerEvaluationService))]
    [InlineData(typeof(LlmTriggerReviewer))]
    public void ConstructorDependencies_ShouldNotReachExecution(Type type)
    {
        List<string> dependencyTypeNames = type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .ToList();

        foreach (string dependency in dependencyTypeNames)
        {
            foreach (string forbidden in _forbiddenTypeFragments)
            {
                dependency.Should().NotContain(
                    forbidden,
                    "the agent-review path only proposes, so {0} must not depend on {1}",
                    type.Name,
                    forbidden);
            }
        }
    }
}
