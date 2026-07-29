using System.Reflection;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// GATE-BELOW-MODEL (gh#402, AGENTS.md "enforcement lives below the model"): the agent-review path <b>proposes</b> —
/// it persists a Suggestion and at most an advisory, and NOTHING else. This asserts, structurally, that no type on the
/// path takes a constructor dependency that could reach execution: no order executor, no risk gate, no venue / ProjectX
/// client, and no <b>kill switch / auto-flatten</b> (which cancel working orders and close positions). A future edit
/// that wired one in would fail here rather than in production. Both reviewer implementations are covered, since the
/// inert one is the ACTIVE reviewer whenever no LLM is configured.
/// </summary>
public class AgentReviewGateBelowModelTests
{
    // Fragments of type names that can place, size, route, gate, or FLATTEN an order/position. The path must reach NONE.
    private static readonly string[] _forbiddenTypeFragments =
    [
        "IOrderExecutor",
        "OrderExecution",
        "IRiskGate",
        "ITradingVenue",
        "IVenueConnection",
        "IAccountEventStream",
        "ProjectX",
        "KillSwitch", // IKillSwitch / KillSwitchService -- flattens positions, cancels working orders, locks trading
        "Flatten",    // AutoFlattenService / FlattenCheckInService / the watchdog -- the pre-close forced exit
    ];

    [Theory]
    [InlineData(typeof(TriggerEvaluationService))]
    [InlineData(typeof(LlmTriggerReviewer))]
    [InlineData(typeof(NullTriggerReviewer))]
    [InlineData(typeof(AnthropicLlmProvider))] // the real provider the reviewer calls (A2, gh#423) -- still no execution reach
    [InlineData(typeof(StubLlmProvider))]      // the inert provider bound when unconfigured
    [InlineData(typeof(AiSpendGovernor))]      // the pure spend gate (gh#448) -- defensive: a cost cap injects no execution type
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
