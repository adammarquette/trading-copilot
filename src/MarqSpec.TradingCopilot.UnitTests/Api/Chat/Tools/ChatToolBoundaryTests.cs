using System.Reflection;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// THE CHAT TOOL BOUNDARY (gh#1134 of gh#1059, extending the gh#925 / gh#930 read-only boundary; ADR-0025,
/// `AGENTS.md` <i>"enforcement lives below the model"</i>): <b>no</b> <see cref="IChatTool"/> — read <i>or</i> write —
/// may reach an order, venue, or gate type. The write tool of this increment <b>proposes</b>; it does not execute,
/// and this fails the build if it ever gains the capability to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it enumerates rather than lists.</b> The theory source is <see cref="AllChatTools"/> — every concrete
/// <see cref="IChatTool"/> in the API assembly, discovered by reflection. A hand-maintained
/// <c>[InlineData(typeof(…))]</c> list (the shape <c>AgentReviewGateBelowModelTests</c> uses, correctly, for a closed
/// path) would leave the <i>next</i> tool uncovered until somebody remembered to add a row — and the tool somebody
/// forgets is exactly the one that slips a venue client in. <see cref="AllChatTools_ShouldEnumerateEveryShippedTool"/>
/// pins that the discovery itself cannot silently find nothing, since a theory over an empty source passes every
/// assertion in this class.
/// </para>
/// <para>
/// <b>Why the write tool's dependencies are pinned exactly, not merely scanned.</b> A fragment scan over direct
/// constructor parameters is defeated by one indirection — a tool taking a helper that itself takes an
/// <c>IOrderExecutor</c> passes it. So <see cref="WriteToolConstructor_ShouldTakeOnlyAllowedCollaborators"/> pins the
/// write tool's dependency <i>set</i>: any new constructor parameter, forbidden-sounding or not, fails until somebody
/// deliberately widens the allow-list — which is a review, not an accident. The read tools keep the fragment guard
/// alone; their dependencies were reviewed in gh#925 / gh#929 / gh#987 and adding to them is not this increment's risk.
/// </para>
/// <para>
/// This is the <b>structural</b> half of the boundary; it fails on a <i>capability</i>. The behavioural half — an
/// order-shaped tool the model invents is never dispatched, and a chat turn moves no venue counter — is the gh#930
/// integration suite, <b>extended</b> for the write tool in the same PR. Neither replaces the other.
/// </para>
/// </remarks>
public class ChatToolBoundaryTests
{
    /// <summary>
    /// Fragments of type names that can place, size, route, gate, or FLATTEN an order or position — deliberately the
    /// same set <c>AgentReviewGateBelowModelTests</c> guards the agent-review path with, so the two "below the model"
    /// paths are held to one definition of reach rather than two that drift apart.
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
    /// The write tool's <b>complete</b> permitted constructor-parameter set (simple names; open generics reflect as
    /// <c>IOptions`1</c> / <c>ILogger`1</c> / <c>DbContextOptions`1</c>). Widening this is a deliberate edit, which is
    /// the whole point: it is the guard a helper cannot smuggle execution past, one indirection down.
    /// </summary>
    private static readonly HashSet<string> _allowedWriteToolCollaborators =
    [
        "DbContextOptions`1",          // the shared options -- the tool builds its OWN owner-scoped context per call
        "ICurrentUser",                // the request's operator (R-20)
        "ISessionDeadlineSource",      // the narrow READ seam onto a market's deadline -- no flatten type crosses it
        "ISuggestionRealtimeNotifier", // presentation-only per-owner push (ADR-0021)
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
                    "a chat tool reads and proposes, so {0} must not depend on {1} — enforcement lives below the model",
                    tool.Name,
                    forbidden);
            }
        }
    }

    /// <summary>
    /// The write tool makes <b>no model call of its own</b>, so the turn's existing per-call <c>AIUsage</c> ledger
    /// already accounts for every billed call a write-tool turn makes (one row per model call, gh#925). A tool that
    /// grew its own <see cref="ILlmProvider"/> would bill spend the governor's floor never sees — refused here rather
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

    [Fact]
    public void WriteToolConstructor_ShouldTakeOnlyAllowedCollaborators()
    {
        ConstructorInfo[] constructors = typeof(GenerateSuggestionTool).GetConstructors();
        constructors.Should().ContainSingle(
            "a tool has exactly one constructor, so the dependency set below is the whole of it");

        List<string> parameterTypeNames = [.. constructors[0].GetParameters().Select(p => p.ParameterType.Name)];

        parameterTypeNames.Should().OnlyContain(
            name => _allowedWriteToolCollaborators.Contains(name),
            "a write tool's dependencies are pinned EXACTLY, not merely scanned for forbidden fragments: one "
            + "indirection through a new helper would defeat a fragment scan, so it may take only {0}",
            string.Join(", ", _allowedWriteToolCollaborators));
    }

    /// <summary>
    /// Every dependency is <b>required</b>, never optional. An optional constructor parameter defaulting to
    /// <see langword="null"/> degrades silently to a no-op when the type is <c>new</c>ed in a test — the staged
    /// proposal would then be sized, moded or expired by whatever the default happened to be, and the guards above
    /// would still pass. So the constructor takes no defaults at all.
    /// </summary>
    [Fact]
    public void WriteToolConstructor_ShouldMakeEveryDependencyRequired()
    {
        typeof(GenerateSuggestionTool).GetConstructors()[0]
            .GetParameters()
            .Should().OnlyContain(
                parameter => !parameter.IsOptional && !parameter.HasDefaultValue,
                "an optional dependency defaults to a silent no-op when the tool is constructed by hand");
    }

    /// <summary>
    /// The discovery itself must be able to fail. A reflection theory that silently finds nothing passes every
    /// assertion above; this pins that the shipped set is really enumerated, naming each tool's type.
    /// </summary>
    [Fact]
    public void AllChatTools_ShouldEnumerateEveryShippedTool()
    {
        IReadOnlyList<Type> discovered = DiscoverChatTools();

        discovered.Should().Contain(
            [
                typeof(QueryJournalTool), typeof(GetQuoteTool), typeof(ReadPositionsTool), typeof(SearchNewsTool),
                typeof(GenerateSuggestionTool),
            ],
            "the boundary theory must really enumerate the shipped read AND write tools — a discovery that found "
            + "nothing would pass every assertion in this class");
        discovered.Should().HaveCountGreaterThanOrEqualTo(
            5, "four read tools (gh#925 / gh#929 / gh#987) plus the write tool (gh#1134)");
    }

    private static IReadOnlyList<Type> DiscoverChatTools() =>
        [.. typeof(IChatTool).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IChatTool)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];
}
