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
    [InlineData(typeof(ReviewEnrichmentSource))] // the deep-tier enrichment reader (gh#476) -- reads market data, reaches no execution
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

    /// <summary>
    /// REVIEWER PURITY (gh#476): the reviewer only <b>proposes</b> from what the scan hands it, so it must stay a pure
    /// judgment seam — dependent on the provider, its options, and a logger, and <b>nothing else</b>. In particular it
    /// must never gain a data-access dependency (a <c>DbContext</c>) or the enrichment source itself: enrichment is
    /// assembled in the scan and arrives on the context, keeping the reviewer decoupled from the store. The execution-
    /// fragment theory above cannot catch this — a <c>DbContext</c> or an <see cref="IReviewEnrichmentSource"/> is not
    /// an "execution" type — so this pins the constructor shape directly. Wiring the enricher into the reviewer (the
    /// tempting "just read it here" shortcut) fails here, by design.
    /// </summary>
    [Fact]
    public void LlmTriggerReviewerConstructor_ShouldDependOnlyOnTheProviderOptionsAndLogger()
    {
        List<string> parameterTypeNames = typeof(LlmTriggerReviewer).GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        // IOptions<LlmOptions> and ILogger<LlmTriggerReviewer> reflect as the open-generic simple names.
        parameterTypeNames.Should().BeEquivalentTo("ILlmProvider", "IOptions`1", "ILogger`1");
    }
}
