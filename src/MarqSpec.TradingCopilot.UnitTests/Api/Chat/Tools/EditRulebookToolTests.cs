using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>edit_rulebook</c> chat write tool (gh#1135 of gh#1059, R-7 / R-6, ADR-0025): the co-pilot turns a practice
/// the operator stated in chat into a durable standing rule — <b>and arms nothing by doing so</b>.
/// </summary>
/// <remarks>
/// <para>
/// The cases are derived from the card's acceptance criteria, not from the implementation. The five they exist for:
/// (1) <b>authorship arms nothing</b> — what is written is <c>Unconfirmed</c> and therefore inert <i>regardless of
/// <c>Enabled</c></i>, and only the operator's own confirm changes that; (2) the model does not get to choose the
/// route, the account, the size, <c>Enabled</c> or the confirmation — those are absent from the schema, not
/// validated; (3) every malformed or refused path writes <b>nothing at all</b> and returns an error string the model
/// reads, never a throw; (4) the write is R-20 owner-scoped, so another operator's rule is invisible rather than
/// merely refused; and (5) a rule carries the conversation it was authored in (gh#471), and fails closed rather than
/// writing an unattributed one.
/// </para>
/// <para>
/// <b>Why (1) is proved against the real scan rather than against the stored enum.</b> Asserting
/// <c>Confirmation == Unconfirmed</c> only re-states the tool's own write. What the card asks for is that nothing
/// <i>fires</i> — so <see cref="ExecuteAsync_ShouldWriteARuleTheScanWillNotFire_EvenEnabledAndSatisfied"/> runs the
/// production <see cref="TriggerEvaluationService"/> over the row the tool actually wrote, with the condition
/// satisfied. Its second half is the <b>control that makes the first half falsifiable</b>: the operator's own
/// confirm is applied and the very same rule then fires, so a green "nothing fired" cannot be the fixture failing to
/// arrange a fireable rule.
/// </para>
/// <para>
/// <b>Why the stored row is the oracle.</b> Every assertion about what was written reloads it from a fresh
/// owner-scoped context. The tool's JSON reply is what the <i>model</i> is told; a tool that reported a confirmation
/// state it did not persist would satisfy an assertion made against its reply, so the reply is checked separately.
/// </para>
/// </remarks>
public class EditRulebookToolTests
{
    private const string Symbol = "ES";
    private const string Indicator = "rsi";
    private const int Period = 14;
    private const int Resolution = 1;

    /// <summary>A coherent authoring call: "tell me when rsi(14) on 1m goes below 30 on ES".</summary>
    private const string AuthorRsiBelow30 =
        "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,"
        + "\"comparison\":\"Below\",\"threshold\":30}";

    private static readonly DateTimeOffset _now = new(2026, 8, 17, 18, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly Guid _conversation = Guid.NewGuid();

    // The scan's collaborators, for the "does it actually fire?" cross-check. Faked because this suite is about the
    // rule the TOOL wrote -- the scan's own behaviour is TriggerEvaluationServiceTests'.
    private readonly IIndicatorSource _indicators = A.Fake<IIndicatorSource>();
    private readonly INotificationChannel _notifications = A.Fake<INotificationChannel>();
    private readonly ITriggerReviewer _reviewer = A.Fake<ITriggerReviewer>();
    private readonly IReviewEnrichmentSource _enrichment = A.Fake<IReviewEnrichmentSource>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();
    private readonly ILlmMetrics _llmMetrics = A.Fake<ILlmMetrics>();
    private readonly ISessionDeadlineSource _deadlines = A.Fake<ISessionDeadlineSource>();
    private readonly ISuggestionRealtimeNotifier _suggestionNotifier = A.Fake<ISuggestionRealtimeNotifier>();
    private readonly IPriceLevelSource _levels = A.Fake<IPriceLevelSource>();
    private readonly IInstrumentSpecSource _specs = A.Fake<IInstrumentSpecSource>();

    public EditRulebookToolTests() =>
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> DbOptions =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid? asUser = null) => new(DbOptions, new FixedUser(asUser ?? _owner));

    private EditRulebookTool Tool(Guid? asUser = null, Guid? inConversation = null)
    {
        ChatTurnScope scope = new();
        scope.EnterConversation(inConversation ?? _conversation);
        return new EditRulebookTool(
            DbOptions,
            new FixedUser(asUser ?? _owner),
            scope,
            new FakeTimeProvider(_now),
            NullLogger<EditRulebookTool>.Instance);
    }

    /// <summary>The tool with <b>no</b> conversation entered — the broken-wiring case it must fail closed on.</summary>
    private EditRulebookTool ToolOutsideATurn() => new(
        DbOptions,
        new FixedUser(_owner),
        new ChatTurnScope(),
        new FakeTimeProvider(_now),
        NullLogger<EditRulebookTool>.Instance);

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The production scan, built over the same in-memory store the tool wrote into.</summary>
    private TriggerEvaluationService Scan() => new(
        Context(), DbOptions, _indicators, _notifications, _reviewer, _enrichment, _ledger, _llmMetrics, _deadlines,
        Options.Create(new SuggestionOptions()), new AiSpendGovernor(), Options.Create(new GovernorOptions()),
        _suggestionNotifier, new SuggestionThrottle(), _levels, _specs,
        Options.Create(new ConfluenceOptions { TimeframeMinutes = [] }),
        NullLogger<TriggerEvaluationService>.Instance);

    private void IndicatorReturns(decimal? value) =>
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(value);

    private async Task<IReadOnlyList<TriggerRecord>> RulesAsync(Guid? forOwner = null)
    {
        await using TradingCopilotDbContext context = Context(forOwner ?? _owner);
        return await context.Triggers.AsNoTracking().ToListAsync();
    }

    private async Task<TriggerRecord> SoleRuleAsync(Guid? forOwner = null) =>
        (await RulesAsync(forOwner)).Should().ContainSingle().Which;

    private async Task<Guid> SeedRuleAsync(
        Guid? owner = null,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed,
        TriggerRoute route = TriggerRoute.Mechanical,
        decimal threshold = 30m,
        IndicatorComparison comparison = IndicatorComparison.Below,
        NotificationSeverity severity = NotificationSeverity.Notify,
        Guid? accountId = null,
        int? size = null,
        int armCycle = 0,
        TriggerArmState armState = TriggerArmState.Armed)
    {
        Guid ownerId = owner ?? _owner;
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Triggers.Add(new TriggerRecord
        {
            Id = id,
            UserId = ownerId,
            Symbol = Symbol,
            Indicator = Indicator,
            Period = Period,
            ResolutionMinutes = Resolution,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = comparison,
            Threshold = threshold,
            Route = route,
            AccountId = accountId,
            Size = size,
            Severity = severity,
            Enabled = true,
            Confirmation = confirmation,
            ArmState = armState,
            ArmCycle = armCycle,
            CreatedAt = _now.AddDays(-1),
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorIn(string json) =>
        Parse(json).TryGetProperty("error", out JsonElement error) ? error.GetString() ?? string.Empty : string.Empty;

    private static string Input(string extraJson) => AuthorRsiBelow30[..^1] + "," + extraJson + "}";

    // =================================================================================================================
    // (1) AUTHORSHIP ARMS NOTHING -- the boundary this whole card exists for.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldWriteARuleTheScanWillNotFire_EvenEnabledAndSatisfied()
    {
        // The rule the model authors is ENABLED -- the tool sets Enabled true, exactly as the authoring endpoint
        // does -- and its condition is satisfied on the very next scan. Nothing may fire, because it is UNCONFIRMED.
        await Tool().ExecuteAsync(AuthorRsiBelow30, CancellationToken.None);
        TriggerRecord authored = await SoleRuleAsync();
        authored.Enabled.Should().BeTrue("the tool authors an enabled rule -- Enabled is not what holds it back");
        authored.Confirmation.Should().Be(TriggerConfirmation.Unconfirmed, "authorship arms nothing (gh#470)");
        IndicatorReturns(25m); // below 30 -- satisfied

        int fires = await Scan().ScanAsync(_now, CancellationToken.None);

        fires.Should().Be(0, "a model-authored rule is inert until the operator confirms it, whatever Enabled says");
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using (TradingCopilotDbContext reload = Context())
        {
            TriggerRecord unread = await reload.Triggers.SingleAsync(rule => rule.Id == authored.Id);
            unread.LastEvaluatedValue.Should().BeNull("the scan never even read an unconfirmed rule");
            unread.LastFiredAt.Should().BeNull();
            (await reload.TriggerFirings.CountAsync()).Should().Be(0, "no firing was journalled");
            (await reload.Suggestions.CountAsync()).Should().Be(0, "and no suggestion was issued");
        }

        // ---- THE CONTROL. The ONLY difference below is the operator's own confirm; if the rule the tool wrote were
        // unfireable for some other reason, the assertions above would be vacuous and this half would go red.
        await using (TradingCopilotDbContext confirming = Context())
        {
            TriggerRecord rule = await confirming.Triggers.SingleAsync(candidate => candidate.Id == authored.Id);
            rule.Confirmation = TriggerConfirmation.Confirmed; // what POST /api/triggers/{id}/confirm does
            await confirming.SaveChangesAsync();
        }

        (await Scan().ScanAsync(_now, CancellationToken.None)).Should().Be(
            0, "a freshly confirmed rule SEEDS silently on its first pass -- it adopts current truth, it does not fire");
        IndicatorReturns(40m);
        await Scan().ScanAsync(_now, CancellationToken.None); // re-arms
        IndicatorReturns(25m);

        (await Scan().ScanAsync(_now, CancellationToken.None)).Should().Be(
            1, "the SAME rule fires once the operator has confirmed it -- confirmation is the only thing that armed it");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTheRuleToUnconfirmed_WhenItAmendsAConfirmedOne()
    {
        // The one path by which a model edit could otherwise reach the live firing set: amending a rule the operator
        // already confirmed. They confirmed the OLD condition, not this one, so the confirmation does not carry over.
        Guid id = await SeedRuleAsync(confirmation: TriggerConfirmation.Confirmed);

        await Tool().ExecuteAsync($"{{\"triggerId\":\"{id}\",\"threshold\":25}}", CancellationToken.None);

        TriggerRecord amended = await SoleRuleAsync();
        amended.Threshold.Should().Be(25m, "the amend applied");
        amended.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed, "an amend DISARMS -- the operator never agreed to this condition");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReSeedTheDebounce_WhenItAmendsARule()
    {
        // The same re-seed the patch endpoint does: a condition that became true under the OLD definition must
        // re-seed silently rather than fire, and a fresh cycle stops the next genuine crossing being deduped away.
        Guid id = await SeedRuleAsync(armState: TriggerArmState.Fired, armCycle: 3);

        await Tool().ExecuteAsync($"{{\"triggerId\":\"{id}\",\"threshold\":25}}", CancellationToken.None);

        TriggerRecord amended = await SoleRuleAsync();
        amended.ArmState.Should().Be(TriggerArmState.Unseeded, "an edited rule re-seeds silently, it does not fire");
        amended.ArmCycle.Should().Be(4, "a fresh incident cycle, or the next genuine crossing is deduped as a repeat");
    }

    // =================================================================================================================
    // (2) WHAT THE MODEL DOES NOT GET TO DECIDE -- removed from the schema, not validated.
    // =================================================================================================================

    [Theory]
    [InlineData("route")]
    [InlineData("account")]
    [InlineData("size")]
    [InlineData("enabled")]
    [InlineData("confirmation")]
    public void Definition_ShouldOfferNoPropertyTheOperatorOwns(string forbidden)
    {
        // The gh#1148 rule a write tool inherits: remove the choice from the schema rather than validating the
        // model's answer to it. A schema the model never sees a field in is a stronger guarantee than a check.
        Tool().Definition.InputSchema.Should().NotContain(
            $"\"{forbidden}\":{{",
            "the model may not choose {0} -- it is the operator's, so it is absent from the schema", forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIgnoreAnEnabledOrConfirmationTheModelSends_WhenAuthoring()
    {
        // Belt and braces on the schema: even if the model invents the properties, the parse never reads them.
        await Tool().ExecuteAsync(Input("\"enabled\":false,\"confirmation\":\"Confirmed\""), CancellationToken.None);

        TriggerRecord authored = await SoleRuleAsync();
        authored.Enabled.Should().BeTrue("Enabled is the tool's, not the model's");
        authored.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed, "a model cannot confirm its own rule -- that is the whole gate");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAuthorTheMechanicalRouteWithNoAccountOrSize()
    {
        // Chat ALERTS; it never issues a sized suggestion against an account. The agent-review route carries an
        // account and a contract size -- the operator's, never the model's -- so chat cannot author one.
        await Tool().ExecuteAsync(
            Input("\"route\":\"AgentReview\",\"accountId\":\"" + Guid.NewGuid() + "\",\"size\":5"),
            CancellationToken.None);

        TriggerRecord authored = await SoleRuleAsync();
        authored.Route.Should().Be(TriggerRoute.Mechanical, "a chat-authored rule alerts, it never proposes a sized trade");
        authored.AccountId.Should().BeNull("no chat-authored rule names which money is at risk");
        authored.Size.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLeaveTheRouteAccountAndSizeAlone_WhenItAmendsAnAgentReviewRule()
    {
        // The amend path is the other way an account/size could arrive. It edits the CONDITION half only, so an
        // operator-authored agent-review rule keeps the money it was pointed at -- and is still disarmed.
        Guid account = Guid.NewGuid();
        Guid id = await SeedRuleAsync(route: TriggerRoute.AgentReview, accountId: account, size: 2);

        await Tool().ExecuteAsync($"{{\"triggerId\":\"{id}\",\"threshold\":25}}", CancellationToken.None);

        TriggerRecord amended = await SoleRuleAsync();
        amended.Route.Should().Be(TriggerRoute.AgentReview, "an amend never re-routes a rule");
        amended.AccountId.Should().Be(account, "nor re-points it at different money");
        amended.Size.Should().Be(2, "nor resizes it");
        amended.Confirmation.Should().Be(TriggerConfirmation.Unconfirmed, "and it is still disarmed");
    }

    // =================================================================================================================
    // (3) FAIL CLOSED -- an error string the model reads, and NOTHING written.
    // =================================================================================================================

    [Theory]
    [InlineData("", "The tool input was empty.")]
    [InlineData("   ", "The tool input was empty.")]
    [InlineData("[1,2]", "The tool input must be a JSON object.")]
    [InlineData("\"a string\"", "The tool input must be a JSON object.")]
    [InlineData("{ not json", "The tool input was not valid JSON.")]
    public async Task ExecuteAsync_ShouldRefuseAndWriteNothing_WhenTheInputIsMalformed(string input, string expected)
    {
        string result = await Tool().ExecuteAsync(input, CancellationToken.None);

        ErrorIn(result).Should().Be(expected, "a malformed input is an error string the model reads, never a throw");
        (await RulesAsync()).Should().BeEmpty("a refused call writes nothing at all");
    }

    [Theory]
    // Every required authoring field, absent or invalid -- each must refuse rather than write a partial rule.
    [InlineData("{}", "symbol")]
    [InlineData("{\"symbol\":\"  \",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Below\",\"threshold\":30}", "symbol")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"macd\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Below\",\"threshold\":30}", "indicator")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":0,\"resolutionMinutes\":1,\"comparison\":\"Below\",\"threshold\":30}", "period")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":0,\"comparison\":\"Below\",\"threshold\":30}", "resolution")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Sideways\",\"threshold\":30}", "comparison")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Below\"}", "threshold")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Below\",\"threshold\":30,\"hysteresis\":0}", "hysteresis")]
    [InlineData("{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,\"comparison\":\"Below\",\"threshold\":30,\"severity\":\"Screaming\"}", "severity")]
    public async Task ExecuteAsync_ShouldRefuseAndWriteNothing_WhenAnAuthoringFieldIsMissingOrInvalid(
        string input, string named)
    {
        string result = await Tool().ExecuteAsync(input, CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("the model is told what was wrong so it can correct itself");
        ErrorIn(result).Should().ContainEquivalentOf(
            named, "a refusal that does not name the field it refused costs the model a round to guess");
        (await RulesAsync()).Should().BeEmpty("nothing is written on any refused path");
    }

    [Theory]
    // The ADR-0019 silent-monitor guard (gh#1007): an rsi threshold at or beyond a bound makes one direction
    // permanently satisfied, so the debounce seeds straight to Fired and the rule looks authored but never alerts.
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)]
    public async Task ExecuteAsync_ShouldRefuseAThresholdOutsideTheIndicatorsRange_ExactlyAsTheEndpointDoes(
        int threshold)
    {
        string result = await Tool().ExecuteAsync(
            "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":1,"
            + $"\"comparison\":\"Below\",\"threshold\":{threshold}}}",
            CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("a permanently-satisfied condition is a silent monitor, not a rule");
        (await RulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseAndChangeNothing_WhenTheAmendedThresholdIsOutOfRange()
    {
        // A refused amend leaves the STORED rule untouched rather than half-edited. Stated precisely, because the
        // red-proof narrowed it: the top-guard ORDERING alone is not what this catches -- an early return before
        // SaveChanges discards the mutations anyway, so reordering the checks below the applies stays green. What it
        // catches is a half-edited row that actually REACHES the database, which is the defect that would matter.
        Guid id = await SeedRuleAsync(severity: NotificationSeverity.Notify);

        string result = await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{id}\",\"threshold\":0,\"severity\":\"Page\"}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty();
        TriggerRecord untouched = await SoleRuleAsync();
        untouched.Threshold.Should().Be(30m, "the refused threshold was not applied");
        untouched.Severity.Should().Be(
            NotificationSeverity.Notify, "and nor was the VALID field beside it -- a refused amend applies nothing");
        untouched.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed, "a refused amend does not even disarm -- it did not happen");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuse_WhenTheTriggerIdIsNotAGuid()
    {
        string result = await Tool().ExecuteAsync("{\"triggerId\":\"the-rsi-one\"}", CancellationToken.None);

        ErrorIn(result).Should().ContainEquivalentOf("triggerId");
        (await RulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuse_WhenNoSuchRuleExists()
    {
        string result = await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{Guid.NewGuid()}\",\"threshold\":25}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("an amend of a rule that is not there refuses rather than authoring one");
        (await RulesAsync()).Should().BeEmpty("and it certainly does not create the rule it could not find");
    }

    // =================================================================================================================
    // (4) R-20 -- another operator's rulebook is INVISIBLE, not merely refused.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldNotAmendAnotherOperatorsRule_AndLeaveItUntouched()
    {
        Guid strangersRule = await SeedRuleAsync(owner: _stranger, confirmation: TriggerConfirmation.Confirmed);

        string result = await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{strangersRule}\",\"threshold\":25}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("a foreign rule is indistinguishable from one that does not exist");
        TriggerRecord theirs = await SoleRuleAsync(_stranger);
        theirs.Threshold.Should().Be(30m, "another operator's rule was not edited");
        theirs.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed, "and it was certainly not DISARMED by a stranger's chat turn");
        (await RulesAsync()).Should().BeEmpty("nor was one authored under the caller as a side effect");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldWriteUnderTheCallersOwnership()
    {
        await Tool().ExecuteAsync(AuthorRsiBelow30, CancellationToken.None);

        (await SoleRuleAsync()).UserId.Should().Be(_owner, "every row a tool writes is the operator's own (R-20)");
        (await RulesAsync(_stranger)).Should().BeEmpty("and is invisible to anybody else");
    }

    // =================================================================================================================
    // (5) PROVENANCE -- gh#471, and fail-closed rather than an unattributed rule.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldStampTheConversationItWasAuthoredIn()
    {
        await Tool().ExecuteAsync(AuthorRsiBelow30, CancellationToken.None);

        TriggerRecord authored = await SoleRuleAsync();
        authored.SourceConversationId.Should().Be(
            _conversation, "\"why does this rule exist?\" must be answerable without a database walk (gh#471)");
        authored.SourceRuleId.Should().BeNull(
            "the R-7 Rule entity is gh#866 and still backlogged -- this seam stays null rather than inventing an id");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRestampTheConversation_WhenARuleIsAmendedInALaterOne()
    {
        Guid id = await SeedRuleAsync();
        Guid later = Guid.NewGuid();

        await Tool(inConversation: later).ExecuteAsync(
            $"{{\"triggerId\":\"{id}\",\"threshold\":25}}", CancellationToken.None);

        (await SoleRuleAsync()).SourceConversationId.Should().Be(
            later, "provenance follows the edit -- this rule's CURRENT definition came from this conversation");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenNoConversationIsInScope()
    {
        // A chat tool only ever runs inside a turn, so a null scope means the wiring is broken. An unattributed rule
        // is exactly the row nobody can later explain, so refuse rather than write one.
        string result = await ToolOutsideATurn().ExecuteAsync(AuthorRsiBelow30, CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("a broken wiring refuses, it does not write an unattributed rule");
        (await RulesAsync()).Should().BeEmpty();
    }

    // =================================================================================================================
    // The tool's identity and what it tells the model.
    // =================================================================================================================

    [Fact]
    public void Name_ShouldBeTheStableToolId() => Tool().Name.Should().Be("edit_rulebook");

    [Fact]
    public void Definition_ShouldMatchTheToolName_AndTellTheModelTheRuleIsInert()
    {
        LlmToolDefinition definition = Tool().Definition;

        definition.Name.Should().Be("edit_rulebook", "the model echoes this name on its call");
        definition.Description.Should().ContainEquivalentOf(
            "confirm", "the model must know from the schema alone that what it writes is not live");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTellTheModelTheRuleIsInert_OnASuccessfulAuthor()
    {
        string result = await Tool().ExecuteAsync(AuthorRsiBelow30, CancellationToken.None);

        JsonElement reply = Parse(result);
        reply.TryGetProperty("error", out _).Should().BeFalse("a coherent rule is authored, not refused");
        reply.GetProperty("triggerId").GetGuid().Should().Be(
            (await SoleRuleAsync()).Id, "the model is given the id it amends the rule by later");
        reply.GetProperty("confirmation").GetString().Should().Be("Unconfirmed");
        reply.GetProperty("inert").GetString().Should().ContainEquivalentOf(
            "confirm", "a model that thinks it armed an alert will tell the operator it did");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreTheCanonicalIndicatorAndSymbol_WhateverCasingTheModelSent()
    {
        await Tool().ExecuteAsync(
            "{\"symbol\":\"es\",\"indicator\":\"RSI\",\"period\":14,\"resolutionMinutes\":1,"
            + "\"comparison\":\"below\",\"threshold\":30}",
            CancellationToken.None);

        TriggerRecord authored = await SoleRuleAsync();
        authored.Indicator.Should().Be(
            RsiIndicator.IndicatorName, "the stored name must be exactly the IIndicatorSource read identity");
        authored.Symbol.Should().Be("ES", "and the symbol the canonical InstrumentId, not the model's casing");
        authored.Comparison.Should().Be(IndicatorComparison.Below);
    }

    // =================================================================================================================
    // gh#1155 -- AN AMEND THAT APPLIES NOTHING MUST REFUSE, NEVER REPORT "amended".
    // =================================================================================================================

    [Theory]
    [InlineData("\"symbol\":\"NQ\"", "symbol")]
    [InlineData("\"indicator\":\"atr\"", "indicator")]
    [InlineData("\"period\":21", "period")]
    [InlineData("\"resolutionMinutes\":5", "resolutionMinutes")]
    public async Task ExecuteAsync_ShouldRefuseAndChangeNothing_WhenAnAmendNamesAnIdentityField(
        string identityJson, string named)
    {
        // The identity -- symbol / indicator / period / resolutionMinutes -- names WHICH rule this is. Changing any
        // of them is authoring a DIFFERENT rule, not editing this one, so a model that sends one must be told, not
        // have it silently ignored while the rest of the call reports success and disarms a live alert.
        Guid id = await SeedRuleAsync(
            confirmation: TriggerConfirmation.Confirmed, armState: TriggerArmState.Fired, armCycle: 3);

        string result = await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{id}\",{identityJson}}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("naming an identity field on an amend is refused, not ignored");
        ErrorIn(result).Should().ContainEquivalentOf(named, "the model is told which field it cannot amend");
        result.Should().NotContainEquivalentOf(
            "amended", "a refused amend must never claim to have amended anything");
        TriggerRecord untouched = await SoleRuleAsync();
        untouched.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed, "a refused amend does not disarm a live alert");
        untouched.ArmState.Should().Be(TriggerArmState.Fired, "nor re-seed the debounce");
        untouched.ArmCycle.Should().Be(3);
        untouched.Symbol.Should().Be(Symbol, "the identity was not changed either");
        untouched.Indicator.Should().Be(Indicator);
        untouched.Period.Should().Be(Period);
        untouched.ResolutionMinutes.Should().Be(Resolution);
    }

    [Theory]
    [InlineData("\"threshold\":\"25\"")]
    [InlineData("\"hysteresis\":\"2.5\"")]
    public async Task ExecuteAsync_ShouldRefuseAndChangeNothing_WhenAnAmendedValueIsAStringTypedNumber(
        string malformedJson)
    {
        // A JSON STRING where a number belongs parses cleanly but is not a Number -- TryGetDecimal cannot read it.
        // Reading that as "the model sent nothing" is the second instance of this defect: a malformed argument
        // disarmed a live alert while changing nothing at all. "Present but unusable" must refuse, not vanish.
        Guid id = await SeedRuleAsync(
            confirmation: TriggerConfirmation.Confirmed, armState: TriggerArmState.Fired, armCycle: 3);

        string result = await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{id}\",{malformedJson}}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("a present-but-unparseable value is a malformed argument, not an absence");
        result.Should().NotContainEquivalentOf("amended");
        TriggerRecord untouched = await SoleRuleAsync();
        untouched.Threshold.Should().Be(30m, "the malformed value was not applied");
        untouched.Hysteresis.Should().BeNull();
        untouched.Confirmation.Should().Be(
            TriggerConfirmation.Confirmed, "the rule was not disarmed for a change that never happened");
        untouched.ArmState.Should().Be(TriggerArmState.Fired);
        untouched.ArmCycle.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseAndChangeNothing_WhenAnAmendNamesNoField()
    {
        // The bare shape: {"triggerId": "..."} and nothing else. Nothing to apply, so nothing may be reported as
        // applied -- in particular, a live alert must not be disarmed for a call that changed nothing about it.
        Guid id = await SeedRuleAsync(
            confirmation: TriggerConfirmation.Confirmed, armState: TriggerArmState.Fired, armCycle: 3);

        string result = await Tool().ExecuteAsync($"{{\"triggerId\":\"{id}\"}}", CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("an amend that names no amendable field is refused, not a silent success");
        result.Should().NotContainEquivalentOf("amended");
        TriggerRecord untouched = await SoleRuleAsync();
        untouched.Confirmation.Should().Be(TriggerConfirmation.Confirmed, "nothing changed, so nothing should disarm");
        untouched.ArmState.Should().Be(TriggerArmState.Fired);
        untouched.ArmCycle.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldApplyEveryAmendableField_AndNothingElse()
    {
        Guid id = await SeedRuleAsync(
            threshold: 30m, comparison: IndicatorComparison.Below, severity: NotificationSeverity.Notify);

        await Tool().ExecuteAsync(
            $"{{\"triggerId\":\"{id}\",\"threshold\":70,\"comparison\":\"Above\",\"hysteresis\":2.5,"
            + "\"severity\":\"Page\"}",
            CancellationToken.None);

        TriggerRecord amended = await SoleRuleAsync();
        amended.Threshold.Should().Be(70m);
        amended.Comparison.Should().Be(IndicatorComparison.Above);
        amended.Hysteresis.Should().Be(2.5m);
        amended.Severity.Should().Be(NotificationSeverity.Page);
        amended.Indicator.Should().Be(Indicator, "an amend edits the condition's VALUES, never its identity");
        amended.Period.Should().Be(Period, "changing the indicator or period is authoring a different rule");
        amended.Symbol.Should().Be(Symbol);
    }
}
