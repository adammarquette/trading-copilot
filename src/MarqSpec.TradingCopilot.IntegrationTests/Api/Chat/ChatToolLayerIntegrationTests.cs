using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Chat;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api.Chat;

/// <summary>
/// Pre-merge integration coverage for the <b>chat tool layer</b> (gh#930 for gh#925 — R-6, ADR-0025, extending the
/// grounded turn of gh#916; <b>extended</b> for the <c>generate_suggestion</c> write tool of gh#1134 and the
/// <c>edit_rulebook</c> write tool of gh#1135) against
/// <b>real Postgres</b>, driven through the shipped endpoint <c>POST /conversations/{id}/turns</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the first surface where <b>the model chooses an action and the app executes it</b>, so the suite is written
/// against the boundary rather than the happy path. Six failure modes are named and guarded: a turn that claims to be
/// grounded but never ran a tool; a model-invented, <b>order-shaped</b> tool that the loop <i>dispatches</i>; a tool
/// loop that never terminates; billed model calls that never reach the AI-usage ledger the spend governor reads; a
/// <b>write</b> tool that reaches an order rather than staging a proposal, or whose proposal arrives already
/// <i>taken</i>; and an <b>incoherent</b> proposal that commits anyway (gh#1134).
/// </para>
/// <para>
/// <b>How the guards are made able to fail.</b> (1) The scripted model can only produce journal text by echoing what
/// the loop fed back (<see cref="ScriptedChatLlmProvider.ToolResultsIn"/>), so a turn that skipped the tool cannot
/// satisfy the grounding assertions. (2) The offered/unoffered cases are <b>one theory over one mechanism</b> — the
/// same fed-back <see cref="LlmToolResult"/> — so the refusal is proven to be a refusal by the control row that shows
/// a real dispatch through the identical assertion. (3) A runaway loop trips
/// <see cref="ScriptedChatLlmProvider.RunawayTripped"/> and goes <i>red</i> instead of hanging. (4) The ledger case
/// scripts <b>distinct</b> token counts per call, so one aggregated row — or a duplicated one — fails where a count
/// alone would pass.
/// </para>
/// <para>
/// <b>The order tripwire is a recorder, not a name check.</b> Every case asserts the adversarial venue recorded no
/// place / modify / cancel / close, and that the database holds no <c>Order</c> row — the mistake §"The guard
/// discipline" cites (PR #140: asserting method *names* never contain "Order") is exactly what this avoids. A verb is
/// witnessed by a counter that would have moved.
/// </para>
/// <para>
/// <b>What the write tool changed here, and what it deliberately did not.</b> The staged-<c>Suggestion</c> count was
/// folded into that same tripwire and is now <b>stated per case</b> rather than assumed zero: every pre-existing case
/// still asserts <b>zero</b> — including the theory rows, so merely <i>offering</i> a write tool stages nothing — and
/// only a case that actually calls <c>generate_suggestion</c> expects one. Nothing was relaxed: an <c>Order</c> row,
/// a venue call, and a <c>SuggestionDisposition</c> stay at zero in <b>every</b> case, the write tool's included,
/// because a proposal is not an execution and staging is not taking.
/// </para>
/// </remarks>
public class ChatToolLayerIntegrationTests : IClassFixture<ChatToolLayerTestPostgresFactory>
{
    /// <summary>The instrument on operator A's seeded trade — the string only a real journal read can produce.</summary>
    private const string InstrumentA = "CON.F.US.MES.U26";

    /// <summary>The instrument on the second operator's seeded trade — must never appear in A's tool result (R-20).</summary>
    private const string InstrumentB = "CON.F.US.MNQ.U26";

    /// <summary>A's realized P&amp;L, distinctive enough that its presence in an answer is not a coincidence.</summary>
    private const decimal RealizedA = 137.25m;

    /// <summary>The second operator's realized P&amp;L.</summary>
    private const decimal RealizedB = -88.50m;

    /// <summary>The service's documented tool-round cap, plus the one streaming round-1 call that precedes it.</summary>
    private const int ExpectedCallsAtTheCap = 5;

    private readonly ChatToolLayerTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);
    private sealed record ErrorResponse(string Error);

    public ChatToolLayerIntegrationTests(ChatToolLayerTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // AC1 — a tool call grounds the turn end to end.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldGroundTheAnswerInTheToolResult_PersistTheAssistantTurn_AndPushItToTheOwner()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        await SeedClosedTradeAsync(ownerId, await AccountForAsync(client), InstrumentA, RealizedA);
        Guid conversationId = await StartConversationAsync(client);

        // The model asks for the journal, then answers ONLY out of what the loop handed back. It has no other source
        // for the instrument or the P&L below: if the tool never ran, or its result never re-entered the
        // conversation, the answer is literally empty of journal data and every assertion here fails.
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool("query_journal", "{\"limit\":5}"));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Your journal shows: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "How did I trade recently?");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a tool-grounded turn completes");
        ChatTurnResponse turn = await ReadAsync<ChatTurnResponse>(response);

        turn.AssistantMessage.Content.Should().Contain(
            InstrumentA, "the answer carries data that only a real journal read against Postgres could have produced");
        turn.AssistantMessage.Content.Should().Contain(
            "137.25", "the realized P&L round-trips through the tool result into the grounded answer");

        // Persistence: the thread really holds the operator's turn and the grounded reply, in order.
        ConversationDetailResponse thread = await ReadConversationAsync(client, conversationId);
        thread.Messages.Should().HaveCount(2, "the turn persists the operator's message and the assistant reply");
        thread.Messages[0].Role.Should().Be(ChatRole.User);
        thread.Messages[1].Role.Should().Be(ChatRole.Assistant);
        thread.Messages[1].Content.Should().Be(
            turn.AssistantMessage.Content, "what was persisted is what was returned — no second, ungrounded answer");

        // The push is per-owner (ADR-0021) and goes through the SHIPPED notifier — this recorder wraps it, never
        // replaces it, so a broken hub composition would still fail here rather than being papered over.
        _factory.Realtime.Messages.Should().ContainSingle("the committed reply is pushed exactly once");
        (Guid pushedOwner, RealtimeChatMessage pushed) = _factory.Realtime.Messages[0];
        pushedOwner.Should().Be(ownerId, "the push is routed to the CONVERSATION's owner (R-20), never broadcast");
        pushed.Content.Should().Be(turn.AssistantMessage.Content, "and it carries the text that actually committed");
        pushed.ConversationId.Should().Be(conversationId, "correlated to the thread the turn was taken in");
        pushed.Role.Should().Be(ChatRole.Assistant, "the pushed turn is the co-pilot's, not an echo of the operator's");

        // The shape of the loop itself: stream, then the rounds that recover and run the tool.
        _factory.Llm.UnscriptedCalls.Should().Be(0, "the turn made no model call the script did not anticipate");
        _factory.Llm.Calls.Should().HaveCount(3, "round 1 streams, the loop re-issues to recover the tool blocks, then answers");
        _factory.Llm.Calls[0].Kind.Should().Be(LlmCallKind.Stream, "a chat turn still token-streams (gh#906 inc 3b)");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle("the one tool call produced exactly one result").Which;
        fedBack.IsError.Should().BeFalse("an offered read tool runs and returns data, not a fail-closed error");

        await AssertNoOrderPathWasReachedAsync();
    }

    // =============================================================================================================
    // AC2 — the core invariant: the co-pilot never reaches an order / write path.
    //
    // One theory, one mechanism. The CONTROL row (query_journal, offered) and the order-shaped rows travel the same
    // code and are read through the same fed-back LlmToolResult, so the refusal is proven to be a refusal rather
    // than the only thing this assertion can ever say. Without that row, "IsError is true" would also pass on a
    // system that dispatched nothing at all, ever.
    // =============================================================================================================

    /// <summary>The offered control, then tools the model invents — each shaped like a real write the broker would take.</summary>
    public static TheoryData<string, string, bool> ToolCallsTheModelMightMake() => new()
    {
        { "query_journal", "{\"limit\":5}", true },
        { "place_order", "{\"instrument\":\"CON.F.US.MES.U26\",\"side\":\"Buy\",\"size\":5,\"orderType\":\"market\"}", false },
        { "modify_order", "{\"venueOrderId\":\"vo-1\",\"stopPrice\":4990.00}", false },
        { "cancel_order", "{\"venueOrderId\":\"vo-1\"}", false },
        { "close_position", "{\"instrument\":\"CON.F.US.MES.U26\"}", false },
        { "flatten_all", "{\"accountId\":\"every\"}", false },
    };

    [Theory]
    [MemberData(nameof(ToolCallsTheModelMightMake))]
    public async Task Turn_ShouldDispatchOnlyToolsFromTheOfferedSet_AndReachNoOrderPathEitherWay(
        string toolName, string inputJson, bool isOffered)
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        await SeedClosedTradeAsync(ownerId, await AccountForAsync(client), InstrumentA, RealizedA);
        Guid conversationId = await StartConversationAsync(client);

        // The provider never consults LlmRequest.Tools before naming one — it demands whatever the row says, exactly
        // as a real model can hallucinate a capability it was never given.
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool(toolName, inputJson));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Tool said: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "Do the thing.");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the turn recovers either way — a refused tool is fed back, it does not crash the turn");

        IReadOnlyList<string> offered = ScriptedChatLlmProvider.OfferedToolNames(_factory.Llm.Calls[1].Request);
        offered.Contains(toolName).Should().Be(
            isOffered, "the row's premise: this case is only meaningful if the tool really was (or was not) offered");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle("one requested call yields one result, dispatched or not").Which;

        if (isOffered)
        {
            // THE CONTROL. Same theory, same mechanism, opposite verdict — this is what makes the refusals below
            // evidence rather than a vacuous assertion.
            fedBack.IsError.Should().BeFalse("an offered read tool is dispatched");
            fedBack.Content.Should().Contain(
                InstrumentA, "and it really ran — the result carries the operator's own journal row from Postgres");
        }
        else
        {
            fedBack.IsError.Should().BeTrue(
                "a tool outside the offered set is NEVER dispatched; the loop feeds a fail-closed error instead");
            fedBack.Content.Should().Contain(
                "unknown tool", "the model is told the tool does not exist, so it can recover or apologise");
            fedBack.Content.Should().NotContain(
                InstrumentA, "an undispatched tool produces no data at all — not even the data a real tool would read");
        }

        // The invariant, witnessed by counters that WOULD have moved: no chat turn reaches the venue or writes an
        // order/suggestion row, whichever tool the model asked for.
        await AssertNoOrderPathWasReachedAsync();
    }

    // =============================================================================================================
    // AC3 — the loop is bounded.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldFailClosedAtTheRoundCap_WhenTheModelNeverStopsAskingForTools()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        await SeedClosedTradeAsync(await OperatorUserIdAsync(), await AccountForAsync(client), InstrumentA, RealizedA);
        Guid conversationId = await StartConversationAsync(client);

        // Deliberately an OFFERED tool: the bound must hold for a model that only ever does legal things, not merely
        // for one that trips the unknown-tool path. Every call is a real dispatch, and the loop must still stop.
        _factory.Llm.ScriptAlways(_ => ScriptedChatLlmProvider.WantsTool("query_journal", "{\"limit\":1}"));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "Keep going forever.");

        // THE bound. Remove the round cap and the provider passes RunawayThreshold, latches this, and throws — so a
        // broken cap surfaces as a red assertion here instead of a test that never returns.
        _factory.Llm.RunawayTripped.Should().BeFalse(
            $"the tool loop must terminate on its own; it made {_factory.Llm.CallCount} model calls");
        _factory.Llm.CallCount.Should().Be(
            ExpectedCallsAtTheCap,
            "the cap is exactly one streaming round plus the service's 4 tool rounds — a changed cap must be a "
            + "deliberate edit here, not a silent drift");

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity, "exhausting the cap fails CLOSED — it is not an answer");
        ErrorResponse error = await ReadAsync<ErrorResponse>(response);
        error.Error.Should().Contain(
            "could not finish", "the operator is told the turn was abandoned, not handed a fabricated reply");

        ConversationDetailResponse thread = await ReadConversationAsync(client, conversationId);
        thread.Messages.Should().ContainSingle(
            "the operator's turn is kept, but NO assistant turn is invented for a turn that never answered");
        thread.Messages[0].Role.Should().Be(ChatRole.User);
        _factory.Realtime.Messages.Should().BeEmpty("nothing committed, so nothing is pushed as the co-pilot's reply");

        // Bounded does not mean unbilled: every one of those calls cost money and the governor floor must see it.
        IReadOnlyList<AiUsageRecord> ledger = await LedgerRowsAsync();
        ledger.Should().HaveCount(
            ExpectedCallsAtTheCap, "each call the abandoned turn made is still ledgered — a fail-closed turn is not free");

        await AssertNoOrderPathWasReachedAsync();
    }

    // =============================================================================================================
    // AC4 — every model call is ledgered.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldLedgerOneAiUsageRowPerModelCall_StampedToTheConversationOwner()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        await SeedClosedTradeAsync(ownerId, await AccountForAsync(client), InstrumentA, RealizedA);
        Guid conversationId = await StartConversationAsync(client);

        // DISTINCT token counts per call. A count of three would also pass on a system that wrote the same row three
        // times, or one aggregated row plus two strays; matching the exact per-call pairs cannot.
        LlmUsage streamUsage = new(InputTokens: 900, OutputTokens: 40);
        LlmUsage toolUsage = new(InputTokens: 1_200, OutputTokens: 60);
        LlmUsage answerUsage = new(InputTokens: 1_500, OutputTokens: 220);

        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse(streamUsage));
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool("query_journal", "{\"limit\":5}", usage: toolUsage));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Your journal shows: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content)),
            answerUsage));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "How did I trade recently?");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<AiUsageRecord> rows = await LedgerRowsAsync();
        rows.Should().HaveCount(3, "a tool-using turn makes several model calls and ledgers EVERY one of them");
        rows.Should().OnlyContain(
            row => row.UserId == ownerId, "every row is stamped to the CONVERSATION's owner (R-20), never request input");
        rows.Should().OnlyContain(
            row => row.Feature == AiUsageFeature.Chat, "chat spend is attributed to Chat — it survives CK_AiUsage_Feature_NotUnknown");
        rows.Should().OnlyContain(row => row.Tier == LlmModelTier.Deep, "a chat turn runs at the deep tier (ADR-0008)");
        rows.Should().OnlyContain(
            row => row.Outcome == AiUsageOutcome.Succeeded, "a tool_use stop is a successful, billed call — not a failure");

        rows.Select(row => (row.InputTokens, row.OutputTokens)).Should().BeEquivalentTo(
            new[]
            {
                (streamUsage.InputTokens, streamUsage.OutputTokens),
                (toolUsage.InputTokens, toolUsage.OutputTokens),
                (answerUsage.InputTokens, answerUsage.OutputTokens),
            },
            "one row PER CALL with that call's own tokens — an aggregate or a duplicate cannot satisfy this");

        // Non-zero, and priced at the pinned deep rates ($3 / $15 per million). A suite that asserted only "a row
        // exists" would be satisfied by $0.00 rows, and the governor floor built on them would be blind.
        rows.Should().OnlyContain(row => row.EstimatedCostUsd > 0m, "a billed call is never ledgered at $0.00");
        rows.Sum(row => row.EstimatedCostUsd).Should().Be(
            0.0156m,
            "the three calls price at 0.0033 + 0.0045 + 0.0078 through numeric(18,8) — the exact figure the daily "
            + "spend window will sum");

        await AssertNoOrderPathWasReachedAsync();
    }

    // =============================================================================================================
    // AC5 — R-20 tool scoping.
    // =============================================================================================================

    [Fact]
    public async Task QueryJournal_ShouldReturnOnlyTheCallingOperatorsTrades()
    {
        await ResetAsync();
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        Guid ownerA = await OperatorUserIdAsync();
        await SeedClosedTradeAsync(ownerA, await AccountForAsync(operatorA), InstrumentA, RealizedA);

        (HttpClient operatorB, Guid ownerB) = await CreateSecondOperatorAsync(operatorA);
        await SeedClosedTradeAsync(ownerB, await AccountForAsync(operatorB), InstrumentB, RealizedB);
        ownerB.Should().NotBe(ownerA, "the two turns below are taken by genuinely different owners");

        // B first. Both directions are asserted, so neither result can be an empty read masquerading as isolation:
        // each operator must SEE its own trade (the positive control) and MISS the other's in the same assertion pair.
        string resultForB = await SoleToolResultContentAsync(operatorB, "What have I traded?");
        resultForB.Should().Contain(InstrumentB, "the second operator's own journal row is returned");
        resultForB.Should().NotContain(
            InstrumentA, "and the first operator's trade is invisible to it — the R-20 filter scopes the tool's read");

        _factory.Llm.Reset();

        string resultForA = await SoleToolResultContentAsync(operatorA, "What have I traded?");
        resultForA.Should().Contain(InstrumentA, "the first operator still sees its own journal row");
        resultForA.Should().NotContain(
            InstrumentB, "and never the second operator's — scoping holds in both directions, not just one");

        await AssertNoOrderPathWasReachedAsync();
    }

    // =============================================================================================================
    // AC5 (gh#1134) — the WRITE tool proposes; it does not execute, and an incoherent proposal never commits.
    //
    // These are the cases the read-only suite could not have: they run the tool that genuinely writes. The pair is one
    // mechanism with opposite verdicts — the same scripted call, the same fed-back result, the same tripwire, differing
    // by one number — so "nothing was staged" below is evidence rather than a sentence that could never fail.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldStageOneUntakenProposal_AndStillReachNoOrderPath_WhenTheModelGeneratesASuggestion()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        await AccountForAsync(client);
        (Guid accountId, string accountName, TradingMode accountMode) = await TradableAccountAsync(ownerId);
        Guid conversationId = await StartConversationAsync(client);

        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool(
            "generate_suggestion", ProposalJson(accountName, stopPrice: "4990.00")));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Staged: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "Propose an MES long.");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a write-tool turn completes like any other");

        // The premise: this tool really is in the offered set, so the dispatch below is a dispatch.
        ScriptedChatLlmProvider.OfferedToolNames(_factory.Llm.Calls[1].Request).Should().Contain(
            "generate_suggestion", "the write tool is offered to the model, or this case proves nothing");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle().Which;
        fedBack.IsError.Should().BeFalse("an offered write tool is dispatched like any other");
        fedBack.Content.Should().Contain(
            "NOT been taken", "the model is told plainly, so it cannot report a trade it did not place");

        Suggestion staged = await _factory.WithDatabaseAsync(database =>
            database.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        staged.UserId.Should().Be(ownerId, "the proposal belongs to the conversation's owner (R-20)");
        staged.AccountId.Should().Be(accountId, "and to the account the tool call named");
        staged.State.Should().Be(SuggestionState.Active, "staged and surfaced — live for the operator to consider");
        staged.Size.Should().Be(
            1, "size is the operator's configured ChatProposalSize, never the model's — the schema has no size at all");
        staged.Mode.Should().Be(accountMode, "the mode is read live off the account (R-14), not chosen by the model");
        staged.ExpiresAt.Should().BeAfter(staged.CreatedAt, "the system's validity window, satisfying the DB CHECK");
        staged.TriggerFiringId.Should().BeNull("no trigger fired — chat is not the scan");
        staged.Origin.Should().Be(
            SuggestionOrigin.Chat,
            "the producer is stamped on the row against real Postgres, where CK_Suggestions_Origin_NotUnknown would "
            + "have refused an unstamped one outright");

        // The tripwire, with the one number this case legitimately moved stated explicitly.
        await AssertNoOrderPathWasReachedAsync(expectedSuggestions: 1);
    }

    [Fact]
    public async Task Turn_ShouldStageNothing_WhenTheModelProposesAnIncoherentGeometry()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        await AccountForAsync(client);
        (_, string accountName, _) = await TradableAccountAsync(await OperatorUserIdAsync());
        Guid conversationId = await StartConversationAsync(client);

        // Identical to the case above but for one number: the protective stop is ABOVE the entry on a long, which no
        // coherent setup has. The write must fail closed against real Postgres rather than commit a broken card.
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool(
            "generate_suggestion", ProposalJson(accountName, stopPrice: "5010.00")));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Result: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "Propose an MES long.");
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the turn recovers — a refused proposal is fed back, it does not crash the turn");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle().Which;
        fedBack.Content.Should().Contain(
            "Nothing was staged", "the model is told why, so it can correct itself rather than claim a proposal");

        await AssertNoOrderPathWasReachedAsync(expectedSuggestions: 0);
    }

    [Fact]
    public async Task Turn_ShouldAuthorOneUnconfirmedRule_AndStillReachNoOrderPath_WhenTheModelEditsTheRulebook()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        Guid conversationId = await StartConversationAsync(client);

        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool(
            "edit_rulebook",
            "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":5,"
            + "\"comparison\":\"Below\",\"threshold\":30}"));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Written: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(
            client, conversationId, "Alert me when ES RSI drops below 30 on the 5-minute.");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a write-tool turn completes like any other");

        // The premise: this tool really is in the offered set, so the dispatch below is a dispatch.
        ScriptedChatLlmProvider.OfferedToolNames(_factory.Llm.Calls[1].Request).Should().Contain(
            "edit_rulebook", "the write tool is offered to the model, or this case proves nothing");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle().Which;
        fedBack.IsError.Should().BeFalse("an offered write tool is dispatched like any other");
        fedBack.Content.Should().Contain(
            "UNCONFIRMED", "the model is told plainly, so it cannot report an alert it did not arm");

        TriggerRecord rule = await _factory.WithDatabaseAsync(database =>
            database.Triggers.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        rule.UserId.Should().Be(ownerId, "the rule belongs to the conversation's owner (R-20)");
        rule.Symbol.Should().Be("ES");
        rule.Indicator.Should().Be("rsi");
        rule.Enabled.Should().BeTrue("the rule is enabled — Enabled is not what holds it back");
        rule.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed,
            "authorship arms nothing, against real Postgres where CK_Triggers_Confirmation would have refused an "
            + "unknown value outright and the scan's predicate takes only Confirmed rows (gh#470)");
        rule.Route.Should().Be(TriggerRoute.Mechanical, "a chat-authored rule alerts; it never proposes a sized trade");
        rule.AccountId.Should().BeNull("and it names no money at all");
        rule.Size.Should().BeNull();
        rule.SourceConversationId.Should().Be(
            conversationId, "the rule records the conversation it was authored in (gh#471)");
        rule.SourceRuleId.Should().BeNull("the R-7 Rule entity is gh#866 and still backlogged");

        // The tripwire, with the one number this case legitimately moved stated explicitly.
        await AssertNoOrderPathWasReachedAsync(expectedTriggers: 1);
    }

    [Fact]
    public async Task Turn_ShouldWriteNoRule_WhenTheModelsRuleWouldNeverAlert()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid conversationId = await StartConversationAsync(client);

        // Identical to the case above but for one number: an rsi threshold of 0 makes "below 0" permanently false and
        // "at or above 0" permanently true — ADR-0019's silent monitor, reached from authoring (gh#1007). It must fail
        // closed against real Postgres, where CK_Triggers_Threshold_InIndicatorRange is the backstop below the tool.
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool(
            "edit_rulebook",
            "{\"symbol\":\"ES\",\"indicator\":\"rsi\",\"period\":14,\"resolutionMinutes\":5,"
            + "\"comparison\":\"Below\",\"threshold\":0}"));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            "Result: " + string.Join(
                " ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(
            client, conversationId, "Alert me when ES RSI drops below zero.");
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the turn recovers — a refused rule is fed back, it does not crash the turn");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle().Which;
        fedBack.Content.Should().NotBeEmpty(
            "the model is told why, so it can correct itself rather than claim it wrote a rule");

        await AssertNoOrderPathWasReachedAsync(expectedTriggers: 0);
    }

    // =============================================================================================================
    // The order tripwire, and the fixtures.
    // =============================================================================================================

    /// <summary>
    /// The core invariant, asserted the way §<i>The guard discipline</i> asks for: against recorders that <b>would
    /// have moved</b>. The venue stub counts every place / modify / cancel / close it is asked for, and the database
    /// is the other write path — so a chat turn that reached either is caught by a moved counter or a new row, not by
    /// a naming convention.
    /// </summary>
    /// <param name="expectedSuggestions">
    /// How many proposals this case legitimately staged — <b>0</b> for every read-tool case, and for the theory rows,
    /// so that merely offering a write tool is proven to stage nothing. A case that calls <c>generate_suggestion</c>
    /// states its own number (gh#1134). This is an EXTENSION, not a relaxation: the number is asserted exactly, and
    /// the order / venue / disposition guards below are unconditional whatever it is.
    /// </param>
    /// <param name="expectedTriggers">
    /// The same, for rules (gh#1135). It joins the count for the same reason the suggestion count did: a second write
    /// tool made a second table reachable from a chat turn, and a tripwire that does not watch it would report "no
    /// write path reached" while one was. <b>Whatever the number, every trigger a chat turn left behind is asserted
    /// UNCONFIRMED</b> — that guard is unconditional, because the count alone would pass on a rule that was armed.
    /// </param>
    private async Task AssertNoOrderPathWasReachedAsync(int expectedSuggestions = 0, int expectedTriggers = 0)
    {
        AdversarialTestProjectXVenueFactory venue = _factory.Venue;
        venue.TotalPlacedOrderCount.Should().Be(0, "a chat turn must never place an order at the venue");
        venue.AllPlacedOrderRequests.Should().BeEmpty("not even a malformed or rejected order request escapes a chat turn");
        venue.ClosePositionCalls.Should().BeEmpty("a chat turn must never flatten or close a position");
        venue.CancelOrderCalls.Should().BeEmpty("a chat turn must never cancel a working order");
        venue.ModifyOrderCalls.Should().BeEmpty("a chat turn must never move a stop or resize an order");

        // ConditionalOrders and StopPlans join the count (gh#1148 review). A tool holding DbContextOptions holds an
        // unrestricted write handle to EVERY table, and a ConditionalOrderRecord is the ONE row in this system that
        // ConditionalFiringService places at the venue with no operator act at all — so counting Orders alone left the
        // tripwire's own prose ("reaches no order path") wider than what it actually watched.
        (int orders, int conditionals, int stopPlans, int suggestions, int dispositions,
                List<TriggerRecord> triggers) =
            await _factory.WithDatabaseAsync(async database => (
                await database.Orders.IgnoreQueryFilters().CountAsync(),
                await database.ConditionalOrders.IgnoreQueryFilters().CountAsync(),
                await database.StopPlans.IgnoreQueryFilters().CountAsync(),
                await database.Suggestions.IgnoreQueryFilters().CountAsync(),
                await database.SuggestionDispositions.IgnoreQueryFilters().CountAsync(),
                await database.Triggers.IgnoreQueryFilters().AsNoTracking().ToListAsync()));
        orders.Should().Be(0, "the chat path writes no Order row — a proposal is not an execution");
        conditionals.Should().Be(
            0, "nor a ConditionalOrder, which the firing service places at the venue with NO operator act");
        stopPlans.Should().Be(0, "nor a StopPlan, which is protective-leg state on a live position");
        suggestions.Should().Be(
            expectedSuggestions,
            "a chat turn stages exactly the proposals the tool it called was asked for, and never one more");
        dispositions.Should().Be(
            0, "and nothing chat stages is ever TAKEN — only the operator's own take disposes a suggestion (R-11)");
        triggers.Should().HaveCount(
            expectedTriggers,
            "a chat turn writes exactly the rules the tool it called was asked for, and never one more");
        // Stated as "none is armed" rather than "all are unconfirmed": OnlyContain FAILS on an empty collection, so
        // that form would have turned every read-tool case -- which legitimately writes no rule at all -- red.
        triggers.Should().NotContain(
            rule => rule.Confirmation != TriggerConfirmation.Unconfirmed,
            "and nothing chat authors is ever ARMED — only the operator's own confirm accepts a rule into the firing "
            + "set (gh#470), whatever Enabled says");
    }

    /// <summary>Runs a scripted tool-using turn in a fresh conversation and returns the content fed back to the model.</summary>
    private async Task<string> SoleToolResultContentAsync(HttpClient client, string prompt)
    {
        Guid conversationId = await StartConversationAsync(client);
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.SignalsToolUse());
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.WantsTool("query_journal", "{\"limit\":50}"));
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer(
            string.Join(" ", ScriptedChatLlmProvider.ToolResultsIn(request).Select(result => result.Content))));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, prompt);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the scoped journal read is a normal, successful turn");

        LlmToolResult fedBack = ScriptedChatLlmProvider.ToolResultsIn(_factory.Llm.Calls[2].Request)
            .Should().ContainSingle().Which;
        fedBack.IsError.Should().BeFalse("the journal read succeeded, so what follows is a scoping assertion, not a failure");
        return fedBack.Content;
    }

    /// <summary>
    /// Clears the per-test state the shared container carries between cases: the script and its recordings, the
    /// realtime pushes, and the rows every assertion counts.
    /// </summary>
    private async Task ResetAsync()
    {
        _factory.Llm.Reset();
        _factory.Realtime.Reset();
        await _factory.WithDatabaseAsync(async database =>
        {
            await database.ChatMessages.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.Conversations.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.Trades.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.AiUsage.IgnoreQueryFilters().ExecuteDeleteAsync();

            // The accounts too, since gh#1134 — per-case hygiene, NOT the fix for what the shared container exposed.
            // Each case builds its own through the production discovery flow and the container kept the previous
            // case's, so several accounts came to carry the same venue NAME. That was not a harness artifact: it is
            // the state two connections genuinely produce, and the tool's name-only matching made it a dead end no
            // input could resolve. The fix is that accounts are now ADDRESSABLE (the labels in
            // GenerateSuggestionTool, and the unit cases that pin the round trip); this reset only keeps each case's
            // fixture its own. Ordered after the rows that reference an account.
            await database.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();

            // And the rulebook, since gh#1135 — the tripwire now counts triggers exactly, so a rule left by the
            // previous case would read as one this case wrote.
            await database.Triggers.IgnoreQueryFilters().ExecuteDeleteAsync();
            return true;
        });
    }

    private Task<IReadOnlyList<AiUsageRecord>> LedgerRowsAsync() => _factory.WithDatabaseAsync(
        async database => (IReadOnlyList<AiUsageRecord>)await database.AiUsage.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    private Task SeedClosedTradeAsync(Guid ownerId, Guid accountId, string instrument, decimal realized) =>
        _factory.WithDatabaseAsync(async database =>
        {
            database.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                UserId = ownerId,
                AccountId = accountId,
                Instrument = instrument,
                Side = OrderSide.Buy,
                Size = 2,
                EntryPrice = 5_000.25m,
                ExitPrice = 5_001.50m,
                RealizedPnL = realized,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow.AddHours(-3),
            });
            await database.SaveChangesAsync();
            return true;
        });

    private async Task<Guid> StartConversationAsync(HttpClient client)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/conversations", new CreateConversationRequest($"tool-layer-{Guid.NewGuid():N}"));
        created.StatusCode.Should().Be(HttpStatusCode.OK, "an authenticated operator may start a conversation");
        return (await ReadAsync<ConversationResponse>(created)).Id;
    }

    private Task<HttpResponseMessage> TakeTurnAsync(HttpClient client, Guid conversationId, string content) =>
        client.PostAsJsonAsync($"/conversations/{conversationId}/turns", new ChatTurnRequest(content));

    private async Task<ConversationDetailResponse> ReadConversationAsync(HttpClient client, Guid conversationId)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri($"/conversations/{conversationId}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<ConversationDetailResponse>(response);
    }

    private async Task<HttpClient> AuthenticatedOperatorClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        login.StatusCode.Should().Be(HttpStatusCode.OK, "the bootstrap operator signs in");
        LoginTokenResponse auth = await ReadAsync<LoginTokenResponse>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Creates a genuinely separate owner through the production invitation flow (gh#8) for the R-20 case.</summary>
    private async Task<(HttpClient Client, Guid UserId)> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"chat-operator-b-{Guid.NewGuid():N}@example.com";
        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync(
            "/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK);
        IssueInvitationResponse invite = await ReadAsync<IssueInvitationResponse>(issue);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OperatorB-Pass123!", "Chat Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse token = await ReadAsync<LoginTokenResponse>(accept);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        Guid userId = await _factory.WithDatabaseAsync(database => database.Users
            .IgnoreQueryFilters()
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync());
        return (client, userId);
    }

    private Task<Guid> OperatorUserIdAsync() => _factory.WithDatabaseAsync(database => database.Users
        .IgnoreQueryFilters()
        .Where(user => user.Email == PostgresApiFactory.OperatorEmail)
        .Select(user => user.Id)
        .SingleAsync());

    /// <summary>
    /// A tradeable account for whoever holds <paramref name="client"/>, built through the production firm →
    /// conventions → connection → discovery flow, because <c>Trade.AccountId</c> is a real foreign key.
    /// </summary>
    private async Task<Guid> AccountForAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Chat-{Guid.NewGuid():N}", FirmType.PropFirm));
        createFirm.StatusCode.Should().Be(HttpStatusCode.OK);
        FirmResponse firm = await ReadAsync<FirmResponse>(createFirm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        createConnection.StatusCode.Should().Be(HttpStatusCode.OK);
        ConnectionResponse connection = await ReadAsync<ConnectionResponse>(createConnection);

        using HttpResponseMessage discover = await client.PostAsync(
            new Uri($"/connections/{connection.Id}/accounts/discover", UriKind.Relative), content: null);
        discover.StatusCode.Should().Be(HttpStatusCode.OK);
        List<AccountResponse> accounts = await ReadAsync<List<AccountResponse>>(discover);
        return accounts.First(account => account.CanTrade).Id;
    }

    /// <summary>
    /// A coherent MES long, varying only in <paramref name="stopPrice"/> — the one number that separates the accepted
    /// proposal from the refused one, so the pair differs by nothing else.
    /// </summary>
    private static string ProposalJson(string accountName, string stopPrice) =>
        "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000.25,\"stopPrice\":" + stopPrice
        + ",\"targetPrice\":5020.50,\"rationale\":\"Reclaimed the overnight low on rising delta.\","
        + "\"confidence\":70,\"account\":\"" + accountName + "\"}";

    /// <summary>
    /// The operator's proposable account, read back from Postgres rather than assumed: only a mode-DECLARED,
    /// venue-tradable, active account may carry a proposal, and the discovery stub deliberately returns accounts that
    /// are not. Naming it in the tool input is also what keeps the case deterministic — the tool refuses to guess
    /// between several, which is the unit suite's subject.
    /// </summary>
    private async Task<(Guid Id, string Name, TradingMode Mode)> TradableAccountAsync(Guid ownerId)
    {
        List<Account> proposable = await _factory.WithDatabaseAsync(async database => await database.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(account => account.UserId == ownerId)
            .Where(account => account.Mode != TradingMode.Undeclared && account.CanTrade && account.IsActive)
            .OrderBy(account => account.Name)
            .ToListAsync());

        proposable.Should().NotBeEmpty(
            "the production discovery flow must leave at least one account a proposal could name");
        Account account = proposable[0];
        return (account.Id, account.Name, account.Mode);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        T? value = await response.Content.ReadFromJsonAsync<T>(_json);
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
