using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>edit_rulebook</c> chat write tool (gh#1059, R-7 / R-6, ADR-0029): the co-pilot turns a stated practice into
/// a durable standing rule — which <b>arms nothing</b>.
/// </summary>
/// <remarks>
/// The load-bearing case here is <see cref="ExecuteAsync_ShouldReturnAnAmendedTriggerToUnconfirmed_EvenWhenTheOperatorHadConfirmedIt"/>:
/// an amend is the one path by which a model edit could otherwise reach the live firing set, since the operator's
/// confirmation was of the <i>old</i> condition. Everything else follows the write-tool posture — inert on creation,
/// mechanical-only, owner-scoped, and fail-closed on anything malformed.
/// </remarks>
public class EditRulebookToolTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _conversation = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private DbContextOptions<TradingCopilotDbContext> ContextOptions =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid asUser) => new(ContextOptions, new FixedUser(asUser));

    private EditRulebookTool Tool(Guid? conversationId = null)
    {
        ChatTurnScope scope = new();
        if ((conversationId ?? _conversation) is { } id)
        {
            scope.EnterConversation(id);
        }

        return new EditRulebookTool(
            ContextOptions, new FixedUser(_owner), scope, new FixedClock(_now), NullLogger<EditRulebookTool>.Instance);
    }

    /// <summary>The tool wired with a scope the endpoint never entered — the broken-wiring case.</summary>
    private EditRulebookTool ToolOutsideATurn() => new(
        ContextOptions, new FixedUser(_owner), new ChatTurnScope(), new FixedClock(_now),
        NullLogger<EditRulebookTool>.Instance);

    private static string Author(
        string symbol = "ES",
        string indicator = "rsi",
        int period = 14,
        int resolutionMinutes = 15,
        string comparison = "Above",
        decimal threshold = 70m,
        decimal? hysteresis = null,
        string? severity = null) =>
        JsonSerializer.Serialize(new
        {
            symbol,
            indicator,
            period,
            resolutionMinutes,
            comparison,
            threshold,
            hysteresis,
            severity,
        });

    private async Task<TriggerRecord> SeedTriggerAsync(
        Guid owner,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed,
        TriggerRoute route = TriggerRoute.Mechanical)
    {
        TriggerRecord trigger = new()
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Symbol = "ES",
            Indicator = RsiIndicator.IndicatorName,
            Period = 14,
            ResolutionMinutes = 15,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = IndicatorComparison.Above,
            Threshold = 70m,
            Route = route,
            AccountId = route == TriggerRoute.AgentReview ? Guid.NewGuid() : null,
            Size = route == TriggerRoute.AgentReview ? 2 : null,
            Severity = NotificationSeverity.Notify,
            Enabled = true,
            Confirmation = confirmation,
            ArmState = TriggerArmState.Armed,
            ArmCycle = 3,
            CreatedAt = _now.AddDays(-1),
        };

        await using TradingCopilotDbContext context = Context(owner);
        context.Triggers.Add(trigger);
        await context.SaveChangesAsync();
        return trigger;
    }

    private async Task<IReadOnlyList<TriggerRecord>> StoredAsync()
    {
        await using TradingCopilotDbContext context = Context(_owner);
        return await context.Triggers.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string? ErrorIn(string json) =>
        Parse(json).TryGetProperty("error", out JsonElement error) ? error.GetString() : null;

    // =================================================================================================================
    // Authoring — and the fact that authoring arms nothing.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldAuthorAnUnconfirmedMechanicalTrigger()
    {
        string result = await Tool().ExecuteAsync(Author(), CancellationToken.None);

        ErrorIn(result).Should().BeNull("a well-formed rule is authored");
        TriggerRecord stored = (await StoredAsync()).Should().ContainSingle().Which;
        stored.UserId.Should().Be(_owner, "the rule is written under the calling operator (R-20)");
        stored.Symbol.Should().Be("ES");
        stored.Indicator.Should().Be(RsiIndicator.IndicatorName, "the indicator name is canonicalised, not echoed");
        stored.Comparison.Should().Be(IndicatorComparison.Above);
        stored.Threshold.Should().Be(70m);

        stored.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed,
            "AUTHORSHIP ARMS NOTHING (gh#470): the rule is inert regardless of Enabled until the operator's own "
            + "POST /{id}/confirm accepts it into the firing set");
        stored.Enabled.Should().BeTrue(
            "and Enabled is deliberately true, so the inertness demonstrably comes from the confirmation gate rather "
            + "than from the rule merely being switched off");
        stored.ArmState.Should().Be(TriggerArmState.Unseeded, "a fresh rule seeds silently rather than firing on sight");

        stored.Route.Should().Be(
            TriggerRoute.Mechanical, "chat alerts; it never authors the sized, account-bound agent-review route");
        stored.AccountId.Should().BeNull("no account rides with a chat-authored rule");
        stored.Size.Should().BeNull("and no contract size — sizing is never the model's");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStampTheConversationAsProvenance_AndLeaveSourceRuleIdNull()
    {
        await Tool().ExecuteAsync(Author(), CancellationToken.None);

        TriggerRecord stored = (await StoredAsync()).Should().ContainSingle().Which;
        stored.SourceConversationId.Should().Be(
            _conversation, "R-7 provenance (gh#471): a chat-authored rule records the conversation it came from");
        stored.SourceRuleId.Should().BeNull(
            "the R-7 Rule entity is gh#866 and deliberately still future work — this increment writes a TriggerRecord "
            + "through the existing agent-authored path rather than committing a Rule schema (ADR-0029)");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosedAndWriteNothing_WhenNoConversationIsInScope()
    {
        string result = await ToolOutsideATurn().ExecuteAsync(Author(), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull(
            "a tool can only run inside a turn, so a missing conversation means broken wiring — not a reason to write "
            + "a rule nobody can later explain");
        (await StoredAsync()).Should().BeEmpty("and nothing unattributed is written");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDefaultTheSeverityToNotify_AndAcceptAnExplicitOne()
    {
        await Tool().ExecuteAsync(Author(), CancellationToken.None);
        (await StoredAsync()).Should().ContainSingle().Which.Severity.Should().Be(
            NotificationSeverity.Notify, "an unstated severity tells the trader once rather than paging them");

        await Tool().ExecuteAsync(Author(severity: "Page"), CancellationToken.None);
        (await StoredAsync()).Should().HaveCount(2).And.Contain(
            trigger => trigger.Severity == NotificationSeverity.Page, "an explicit severity is honoured");
    }

    // =================================================================================================================
    // Amending — the path that must DISARM.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldReturnAnAmendedTriggerToUnconfirmed_EvenWhenTheOperatorHadConfirmedIt()
    {
        TriggerRecord confirmed = await SeedTriggerAsync(_owner, TriggerConfirmation.Confirmed);

        string result = await Tool().ExecuteAsync(
            JsonSerializer.Serialize(new { triggerId = confirmed.Id, threshold = 80m }), CancellationToken.None);

        ErrorIn(result).Should().BeNull("the amend is well-formed");
        TriggerRecord stored = (await StoredAsync()).Should().ContainSingle().Which;
        stored.Threshold.Should().Be(80m, "the edit applied");
        stored.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed,
            "THE DISARM: the operator confirmed the OLD condition, not this one, so a model amend drops the rule back "
            + "to inert and awaits a fresh confirmation — otherwise an amend would be the one path by which a model "
            + "change reaches the live firing set");
        stored.ArmState.Should().Be(TriggerArmState.Unseeded, "an edited rule re-seeds silently rather than firing");
        stored.ArmCycle.Should().Be(4, "and a fresh incident cycle stops the next genuine crossing being deduped away");
        stored.SourceConversationId.Should().Be(_conversation, "the rule's current definition came from this conversation");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotChangeTheRouteAccountOrSize_WhenAmendingAnAgentReviewTrigger()
    {
        TriggerRecord agentReview = await SeedTriggerAsync(
            _owner, TriggerConfirmation.Confirmed, TriggerRoute.AgentReview);

        await Tool().ExecuteAsync(
            JsonSerializer.Serialize(new
            {
                triggerId = agentReview.Id,
                threshold = 80m,
                route = "Mechanical",
                accountId = Guid.NewGuid(),
                size = 25,
            }),
            CancellationToken.None);

        TriggerRecord stored = (await StoredAsync()).Should().ContainSingle().Which;
        stored.Route.Should().Be(TriggerRoute.AgentReview, "the route is not the model's to change");
        stored.AccountId.Should().Be(agentReview.AccountId, "nor which account a fire would issue against");
        stored.Size.Should().Be(agentReview.Size, "nor the contract size — the operator's, never the model's");
        stored.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed, "and the amend still disarms it, so nothing fires on the new condition");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseAnotherOperatorsTrigger_AndChangeNothing()
    {
        Guid stranger = Guid.NewGuid();
        TriggerRecord theirs = await SeedTriggerAsync(stranger, TriggerConfirmation.Confirmed);

        string result = await Tool().ExecuteAsync(
            JsonSerializer.Serialize(new { triggerId = theirs.Id, threshold = 80m }), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull("R-20: another operator's rule is invisible, so it reads as absent");

        await using TradingCopilotDbContext context = Context(stranger);
        TriggerRecord unchanged = await context.Triggers.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(trigger => trigger.Id == theirs.Id);
        unchanged.Threshold.Should().Be(70m, "and their rule is untouched");
        unchanged.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed, "in particular it is not disarmed by a stranger's tool call");
    }

    // =================================================================================================================
    // Fail closed — a refused edit leaves the stored rule exactly as it was.
    // =================================================================================================================

    /// <summary>Authoring inputs the model can genuinely produce, each of which must write nothing.</summary>
    public static TheoryData<string, string> MalformedAuthorInputs() => new()
    {
        { "not json at all", "unparseable input" },
        { "[]", "a JSON array rather than an object" },
        { "{}", "no condition at all" },
        { "{\"symbol\":\"ES\",\"indicator\":\"macd\",\"period\":14,\"resolutionMinutes\":15,\"comparison\":\"Above\",\"threshold\":70}", "an indicator the platform does not read" },
        { "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":0,\"resolutionMinutes\":15,\"comparison\":\"Above\",\"threshold\":70}", "a non-positive period" },
        { "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":0,\"comparison\":\"Above\",\"threshold\":70}", "a non-positive resolution" },
        { "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":15,\"comparison\":\"Sideways\",\"threshold\":70}", "an invented comparison" },
        { "{\"symbol\":\" \",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":15,\"comparison\":\"Above\",\"threshold\":70}", "a blank symbol" },
        { "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":15,\"comparison\":\"Above\",\"threshold\":70,\"hysteresis\":0}", "a non-positive hysteresis band" },
        { "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":15,\"comparison\":\"Above\",\"threshold\":140}", "a threshold outside the indicator's meaningful range (ADR-0019's silent monitor, gh#1007)" },
        { "{\"triggerId\":\"not-a-guid\",\"threshold\":80}", "an unparseable trigger id" },
    };

    [Theory]
    [MemberData(nameof(MalformedAuthorInputs))]
    public async Task ExecuteAsync_ShouldReturnAnErrorAndWriteNothing_WhenTheInputIsMalformed(string input, string why)
    {
        string result = await Tool().ExecuteAsync(input, CancellationToken.None);

        ErrorIn(result).Should().NotBeNull($"the tool fails closed with an error result rather than throwing on {why}");
        (await StoredAsync()).Should().BeEmpty("nothing is written on a refused input");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLeaveTheStoredRuleUntouched_WhenAnAmendIsRefused()
    {
        TriggerRecord confirmed = await SeedTriggerAsync(_owner, TriggerConfirmation.Confirmed);

        string result = await Tool().ExecuteAsync(
            JsonSerializer.Serialize(new { triggerId = confirmed.Id, threshold = 140m }), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull("an RSI threshold of 140 can never be crossed — it would be a silent monitor");
        TriggerRecord stored = (await StoredAsync()).Should().ContainSingle().Which;
        stored.Threshold.Should().Be(70m, "the top-guard shape: a refused amend leaves the rule whole, not half-edited");
        stored.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed,
            "and it is NOT disarmed either — a refused edit changed nothing at all, so the operator's live rule keeps "
            + "working rather than being silently switched off by a malformed model call");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTellTheModelTheRuleIsInert()
    {
        string result = await Tool().ExecuteAsync(Author(), CancellationToken.None);

        Parse(result).GetProperty("confirmation").GetString().Should().Be(
            nameof(TriggerConfirmation.Unconfirmed),
            "the model is told the rule is unconfirmed, so it cannot tell the trader their alert is live");
        Parse(result).GetProperty("inert").GetString().Should().Contain("confirm");
    }
}
