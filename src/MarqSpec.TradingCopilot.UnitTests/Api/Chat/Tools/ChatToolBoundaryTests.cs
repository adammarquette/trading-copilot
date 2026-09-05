using System.Reflection;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// THE CHAT TOOL BOUNDARY (gh#1059, extending the gh#925/gh#930 read-only boundary; ADR-0025 → ADR-0029, AGENTS.md
/// "enforcement lives below the model"): <b>no</b> <see cref="IChatTool"/> — read <i>or</i> write — may reach an
/// order, venue, or gate type. The write tools of gh#1059 propose; they do not execute, and this fails the build if
/// one ever gains the capability to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it enumerates rather than lists.</b> The theory source is <see cref="AllChatTools"/> — every concrete
/// <see cref="IChatTool"/> in the API assembly, discovered by reflection. A hand-maintained
/// <c>[InlineData(typeof(...))]</c> list (the shape <c>AgentReviewGateBelowModelTests</c> uses, correctly, for a
/// closed path) would leave the <i>next</i> tool uncovered until somebody remembered to add a row — and the tool
/// somebody forgets is exactly the one that slips a venue client in. Discovery inverts that: a new tool is guarded
/// the moment it compiles, and <see cref="AllChatTools_ShouldFindEveryShippedTool"/> pins that discovery itself
/// cannot silently find nothing.
/// </para>
/// <para>
/// <b>Why it also pins the collaborator allow-list.</b> A fragment scan over direct constructor parameters is
/// defeated by one indirection — a tool taking a helper that takes <c>IOrderExecutor</c> passes it. So
/// <see cref="WriteToolConstructors_ShouldTakeOnlyAllowedCollaborators"/> pins the write tools' dependency
/// <i>set</i> exactly: any new constructor parameter, forbidden-sounding or not, fails until somebody deliberately
/// widens the list — which is a review, not an accident. The read tools keep the fragment guard alone, since their
/// dependencies were reviewed in gh#925/#930 and adding to them is not this increment's risk.
/// </para>
/// <para>
/// This is the <b>structural</b> half of the boundary. The behavioural half — an order-shaped tool the model invents
/// is never dispatched, and a chat turn moves no venue counter — is the gh#930 integration suite, extended in the
/// same PR. Neither replaces the other: this one fails at compile-and-test time on a <i>capability</i>, that one
/// fails on a <i>counter that would have moved</i>.
/// </para>
/// </remarks>
public class ChatToolBoundaryTests
{
    /// <summary>
    /// Fragments of type names that can place, size, route, gate, or FLATTEN an order or position — the same set
    /// <c>AgentReviewGateBelowModelTests</c> guards the agent-review path with, so the two "below the model" paths
    /// are held to one definition of reach rather than two that drift.
    /// </summary>
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

    /// <summary>
    /// The write tools' <b>complete</b> permitted constructor-parameter set (simple names; open generics reflect as
    /// <c>IOptions`1</c> / <c>ILogger`1</c> / <c>DbContextOptions`1</c>). Widening this is a deliberate edit, which
    /// is the point: it is the guard that a helper cannot smuggle execution in one indirection down.
    /// </summary>
    private static readonly HashSet<string> _allowedWriteToolCollaborators =
    [
        "DbContextOptions`1",             // the shared context options -- the tool builds its OWN owner-scoped context
        "ICurrentUser",                   // the request's operator (R-20)
        "ISessionDeadlineSource",         // the narrow READ seam onto a market's deadline -- no flatten type crosses it
        "ISuggestionRealtimeNotifier",    // presentation-only per-owner push (ADR-0021)
        "IChatTurnScope",                 // which conversation the turn is in -- provenance, no capability
        "TimeProvider",
        "IOptions`1",
        "ILogger`1",
    ];

    /// <summary>Every concrete chat tool the API ships — the theory source, so a new tool is guarded on sight.</summary>
    public static TheoryData<Type> AllChatTools()
    {
        TheoryData<Type> tools = [];
        foreach (Type tool in DiscoverChatTools())
        {
            tools.Add(tool);
        }

        return tools;
    }

    /// <summary>The two write tools of gh#1059 — the ones whose dependency set is pinned exactly, not just scanned.</summary>
    public static TheoryData<Type> WriteTools() => new() { typeof(GenerateSuggestionTool), typeof(EditRulebookTool) };

    [Theory]
    [MemberData(nameof(AllChatTools))]
    public void ConstructorDependencies_ShouldNotReachAnOrderVenueOrGateType(Type tool)
    {
        List<string> dependencyTypeNames = tool.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .ToList();

        foreach (string dependency in dependencyTypeNames)
        {
            foreach (string forbidden in _forbiddenTypeFragments)
            {
                dependency.Should().NotContain(
                    forbidden,
                    "a chat tool proposes and reads, so {0} must not depend on {1} — enforcement lives below the model",
                    tool.Name,
                    forbidden);
            }
        }
    }

    /// <summary>
    /// The discovery itself must be able to fail. A reflection theory that silently finds nothing passes every
    /// assertion above; this pins that the shipped set is really enumerated, naming the tools by their stable ids.
    /// </summary>
    [Fact]
    public void AllChatTools_ShouldFindEveryShippedTool()
    {
        IReadOnlyList<Type> discovered = DiscoverChatTools();

        discovered.Should().Contain(
            [
                typeof(QueryJournalTool), typeof(GetQuoteTool), typeof(ReadPositionsTool), typeof(SearchNewsTool),
                typeof(GenerateSuggestionTool), typeof(EditRulebookTool),
            ],
            "the boundary theory must really enumerate the shipped read AND write tools — a discovery that found "
            + "nothing would pass every assertion in this class");
        discovered.Should().HaveCountGreaterThanOrEqualTo(
            6, "four read tools (gh#925/#929/#987) plus the two write tools (gh#1059)");
    }

    [Theory]
    [MemberData(nameof(WriteTools))]
    public void WriteToolConstructors_ShouldTakeOnlyAllowedCollaborators(Type tool)
    {
        ConstructorInfo[] constructors = tool.GetConstructors();
        constructors.Should().ContainSingle(
            "a tool has exactly one constructor, so the dependency set below is the whole of it");

        List<string> parameterTypeNames = constructors[0]
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        parameterTypeNames.Should().OnlyContain(
            name => _allowedWriteToolCollaborators.Contains(name),
            "a write tool's dependencies are pinned EXACTLY, not merely scanned for forbidden fragments: one "
            + "indirection through a new helper would defeat a fragment scan, so {0} may take only {1}",
            tool.Name,
            string.Join(", ", _allowedWriteToolCollaborators));
    }

    /// <summary>
    /// The write tools make <b>no model call</b>, so the turn's existing per-call <c>AIUsage</c> ledger already
    /// accounts for every billed call a write-tool turn makes (one row per model call, gh#925). A tool that grew its
    /// own <see cref="ILlmProvider"/> would bill spend the governor's floor never sees — so it is refused here rather
    /// than discovered in a month of unexplained cost.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllChatTools))]
    public void ConstructorDependencies_ShouldNotTakeAnLlmProvider(Type tool)
    {
        List<Type> dependencies = tool.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        dependencies.Should().NotContain(
            typeof(ILlmProvider),
            "a tool that called the model itself would bill spend outside the turn's per-call AIUsage ledger");
    }

    private static IReadOnlyList<Type> DiscoverChatTools() =>
        [.. typeof(IChatTool).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IChatTool)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];
}
