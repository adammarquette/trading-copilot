using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat;

/// <summary>
/// The grounded chat turn (gh#906, R-6): <c>POST /conversations/{id}/turns</c>. It appends the operator's turn,
/// governor-gates + runs the model, meters + ledgers the call (fail-open), appends the reply, and pushes it to the
/// owner — the R-20 404, the governor block, and the fail-closed/fail-open behaviours are what these pin.
/// </summary>
public class ChatTurnEndpointTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IChatTurnService _turn = A.Fake<IChatTurnService>();
    private readonly IContextRetrievalService _retrieval = A.Fake<IContextRetrievalService>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();
    private readonly ILlmMetrics _metrics = A.Fake<ILlmMetrics>();
    private readonly IChatRealtimeNotifier _notifier = A.Fake<IChatRealtimeNotifier>();
    private readonly IAiSpendGovernor _governor = new AiSpendGovernor();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private static DateTimeOffset At(int minute) => new(2026, 8, 15, 18, minute, 0, TimeSpan.Zero);

    private static AiCallCost Cost(AiUsageOutcome outcome = AiUsageOutcome.Succeeded, decimal usd = 0.01m) =>
        new(AiUsageFeature.Chat, "claude-sonnet-5", LlmModelTier.Deep, outcome, 100, 20, usd, TimeSpan.FromMilliseconds(50));

    private void TurnReturns(ChatTurnResult result) =>
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).Returns(result);

    // Makes the fake turn service stream the given deltas to the endpoint's onDelta (which pushes to the hub), then
    // return the result -- so a test can assert the endpoint forwarded each delta.
    private void TurnStreams(ChatTurnResult result, params string[] deltas) =>
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<ChatMessage> _, IReadOnlyList<RetrievedContextItem> _, Func<string, CancellationToken, Task> onDelta, CancellationToken ct) =>
                EmitAsync(onDelta, ct, result, deltas));

    private static async Task<ChatTurnResult> EmitAsync(
        Func<string, CancellationToken, Task> onDelta, CancellationToken ct, ChatTurnResult result, string[] deltas)
    {
        foreach (string delta in deltas)
        {
            await onDelta(delta, ct);
        }

        return result;
    }

    // Captures the grounding the endpoint hands to StreamAsync, so a test can assert what (if anything) it grounded on.
    private IReadOnlyList<RetrievedContextItem>? _capturedGrounding;

    private void TurnReturnsCapturingGrounding(ChatTurnResult result) =>
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .Invokes((IReadOnlyList<ChatMessage> _, IReadOnlyList<RetrievedContextItem> grounding, Func<string, CancellationToken, Task> _, CancellationToken _) => _capturedGrounding = grounding)
            .Returns(result);

    private void RetrieverReturns(params RetrievedContextItem[] items) =>
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<RetrievedContextItem>)[.. items]);

    private static RetrievedContextItem Item(string headline = "NVDA beats") =>
        new(RetrievalKind.News, headline, ["finnhub"], At(0), "revenue up 40%");

    private async Task<Guid> SeedConversationAsync(Guid? owner = null)
    {
        Guid id = Guid.NewGuid();
        Guid u = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(u);
        context.Conversations.Add(new Conversation { Id = id, UserId = u, Title = "seed", CreatedAt = At(0), UpdatedAt = At(0) });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task SeedSpendAsync(decimal usd, DateTimeOffset at)
    {
        await using TradingCopilotDbContext context = Context();
        context.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            Model = "claude-sonnet-5",
            Feature = AiUsageFeature.Chat,
            Outcome = AiUsageOutcome.Succeeded,
            EstimatedCostUsd = usd,
            OccurredAt = at,
        });
        await context.SaveChangesAsync();
    }

    private Task<IResult> Invoke(
        Guid id, string content, DateTimeOffset now, GovernorOptions? governor = null, IChatTurnGuard? guard = null) =>
        ChatEndpoints.TurnAsync(
            id, new ChatTurnRequest(content), now, Context(),
            _turn, _retrieval, _governor, Options.Create(governor ?? new GovernorOptions()),
            _ledger, _metrics, _notifier, guard ?? FreeGuard(), NullLoggerFactory.Instance, default);

    /// <summary>
    /// The default double for <see cref="IChatTurnGuard"/>: the conversation is free, so it simply invokes the
    /// callback. The real serialization is a Postgres advisory lock and cannot run on the in-memory provider — what
    /// the unit tier proves is that the endpoint runs the WHOLE turn inside the callback and refuses on the busy
    /// path; the cross-request lock itself is QA's on the container-backed tier (the <c>IAccountEntryGuard</c>
    /// pattern, gh#531).
    /// </summary>
    private static IChatTurnGuard FreeGuard()
    {
        IChatTurnGuard guard = A.Fake<IChatTurnGuard>();
        A.CallTo(() => guard.TryRunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<Func<IResult>>._,
                A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<IResult>> turn, Func<IResult> _,
                CancellationToken _) => turn());
        return guard;
    }

    /// <summary>The busy path: another turn holds the conversation, so the callback never runs.</summary>
    private static IChatTurnGuard BusyGuard()
    {
        IChatTurnGuard guard = A.Fake<IChatTurnGuard>();
        A.CallTo(() => guard.TryRunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<Func<IResult>>._,
                A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<IResult>> _, Func<IResult> onBusy,
                CancellationToken _) => Task.FromResult(onBusy()));
        return guard;
    }

    /// <summary>
    /// A guard double that really serializes, in process: one <see cref="SemaphoreSlim"/> per conversation, taken
    /// NON-BLOCKING so a second holder runs <c>onBusy</c> rather than queueing. It is not the production lock (that
    /// is Postgres-evaluated, since two HTTP requests share nothing in process), but it has the production lock's
    /// semantics, which is what lets the interleaving test below drive two genuinely concurrent turns.
    /// </summary>
    private sealed class SerializingChatTurnGuard : IChatTurnGuard
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

        public async Task<T> TryRunExclusiveAsync<T>(
            TradingCopilotDbContext database, Guid conversationId, Func<Task<T>> turn, Func<T> onBusy,
            CancellationToken cancellationToken)
        {
            SemaphoreSlim conversationLock = _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
            if (!await conversationLock.WaitAsync(0, cancellationToken))
            {
                return onBusy();
            }

            try
            {
                return await turn();
            }
            finally
            {
                conversationLock.Release();
            }
        }
    }

    [Fact]
    public async Task TurnAsync_ShouldPersistBothTurns_MeterAndLedger_PushToOwner_AndReturnThem_OnSuccess()
    {
        Guid id = await SeedConversationAsync();
        AiCallCost cost = Cost();
        TurnReturns(new ChatTurnResult(true, "here is the read", [cost]));

        IResult result = await Invoke(id, "what's the ES read?", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ChatTurnResponse response = ValueOf<ChatTurnResponse>(result);
        response.UserMessage.Content.Should().Be("what's the ES read?");
        response.UserMessage.Sequence.Should().Be(1);
        response.UserMessage.Role.Should().Be(ChatRole.User);
        response.AssistantMessage.Content.Should().Be("here is the read");
        response.AssistantMessage.Sequence.Should().Be(2);
        response.AssistantMessage.Role.Should().Be(ChatRole.Assistant);

        A.CallTo(() => _metrics.RecordLlmCall(cost)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _ledger.RecordAsync(
            A<AiUsageEntry>.That.Matches(entry => entry.UserId == _operator && entry.Cost == cost && entry.OccurredAt == At(3)),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.MessageAppendedAsync(
            _operator,
            A<RealtimeChatMessage>.That.Matches(m => m.Role == ChatRole.Assistant && m.Sequence == 2 && m.Content == "here is the read"),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.CountAsync()).Should().Be(2);
        (await verify.Conversations.SingleAsync()).UpdatedAt.Should().Be(At(3)); // bumped for the list read
    }

    [Fact]
    public async Task TurnAsync_ShouldPushEachStreamedDelta_ToTheOwner_DuringTheTurn()
    {
        Guid id = await SeedConversationAsync();
        TurnStreams(new ChatTurnResult(true, "Hello", [Cost()]), "Hel", "lo");

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        // Each token delta reaches the conversation's owner as a realtimeChatChunk during the turn (inc 3b).
        A.CallTo(() => _notifier.ChunkAsync(
            _operator, A<RealtimeChatChunk>.That.Matches(c => c.ConversationId == id && c.Delta == "Hel"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.ChunkAsync(
            _operator, A<RealtimeChatChunk>.That.Matches(c => c.Delta == "lo"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldStillSucceed_WhenAStreamedChunkPushThrows()
    {
        Guid id = await SeedConversationAsync();
        TurnStreams(new ChatTurnResult(true, "Hello", [Cost()]), "Hel");
        A.CallTo(() => _notifier.ChunkAsync(A<Guid>._, A<RealtimeChatChunk>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("hub down"));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK); // a per-chunk push is fail-open, never fails the turn
    }

    [Fact]
    public async Task TurnAsync_ShouldReturn404_AndMakeNoCall_ForAForeignConversation()
    {
        Guid foreign = await SeedConversationAsync(owner: Guid.NewGuid());

        IResult result = await Invoke(foreign, "sneak in", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReject_WhenContentIsBlank()
    {
        Guid id = await SeedConversationAsync();

        IResult result = await Invoke(id, "   ", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReject_WhenContentExceedsTheCap()
    {
        Guid id = await SeedConversationAsync();
        string tooLong = new('x', ChatMessage.ContentMaxLength + 1);

        IResult result = await Invoke(id, tooLong, At(3));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReturn429_AndMakeNoCall_AndPersistNothing_WhenTheDailyBudgetIsReached()
    {
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 25m, at: At(1)); // today's spend already over the 10 USD cap

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status429TooManyRequests);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.AnyAsync()).Should().BeFalse(); // a governor block persists nothing
    }

    [Fact]
    public async Task TurnAsync_ShouldProceed_WhenSpendIsUnderTheBudget()
    {
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 2m, at: At(1));
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldPersistTheUserTurn_LedgerTheCost_ButPersistNoAssistant_OnAFailedTurn()
    {
        Guid id = await SeedConversationAsync();
        AiCallCost cost = Cost(AiUsageOutcome.Failed, usd: 0m);
        TurnReturns(new ChatTurnResult(false, "the co-pilot could not answer", [cost]));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _metrics.RecordLlmCall(cost)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>.That.Matches(entry => entry.Cost == cost), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.MessageAppendedAsync(A<Guid>._, A<RealtimeChatMessage>._, A<CancellationToken>._))
            .MustNotHaveHappened();

        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.ToListAsync()).Should().ContainSingle().Which.Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public async Task TurnAsync_ShouldStillSucceed_WhenTheLedgerWriteThrows()
    {
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("ledger db down"));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK); // fail-open: a ledger fault never fails the turn
        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task TurnAsync_ShouldStillSucceed_WhenTheRealtimePushThrows()
    {
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));
        A.CallTo(() => _notifier.MessageAppendedAsync(A<Guid>._, A<RealtimeChatMessage>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("hub down"));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK); // presentation-only: a push fault never fails the turn
    }

    [Fact]
    public async Task TurnAsync_ShouldMeterAndLedgerEveryModelCall_WhenTheTurnMadeSeveral()
    {
        // A tool-using turn makes several model calls -> several cost rows (gh#925); the endpoint must meter + ledger
        // EACH so the governor floor sees them all. A regression to first/last would silently under-count the budget.
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "grounded answer", [Cost(usd: 0.01m), Cost(usd: 0.02m), Cost(usd: 0.03m)]));

        IResult result = await Invoke(id, "what's my ES read?", At(4));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _metrics.RecordLlmCall(A<AiCallCost>._)).MustHaveHappened(3, Times.Exactly);
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task TurnAsync_ShouldStillLedgerLaterCalls_WhenAnEarlierLedgerWriteFaults()
    {
        // Fail-open PER cost: a ledger fault on one call is logged and the rest still record; the turn stands (200).
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "answer", [Cost(usd: 0.01m), Cost(usd: 0.02m)]));
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("ledger down")).Once();

        IResult result = await Invoke(id, "hi", At(5));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustHaveHappened(2, Times.Exactly);
    }

    // --- Always-on news grounding (gh#995, ADR-0027) ---

    [Fact]
    public async Task TurnAsync_ShouldGroundTheTurn_WhenTheRetrieverReturnsItems()
    {
        Guid id = await SeedConversationAsync();
        RetrieverReturns(Item("NVDA beats"));
        TurnReturnsCapturingGrounding(new ChatTurnResult(true, "grounded answer", [Cost()]));

        IResult result = await Invoke(id, "what's moving ES?", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        // The retriever is asked about the operator's message, and its items reach StreamAsync as the turn's grounding.
        A.CallTo(() => _retrieval.RetrieveAsync(
                "what's moving ES?", A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _capturedGrounding.Should().ContainSingle().Which.Title.Should().Be("NVDA beats");
    }

    [Fact]
    public async Task TurnAsync_ShouldStillSucceedHistoryOnly_WhenTheRetrieverThrows()
    {
        Guid id = await SeedConversationAsync();
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("retrieval down"));
        TurnReturnsCapturingGrounding(new ChatTurnResult(true, "ok", [Cost()]));

        IResult result = await Invoke(id, "hi", At(3));

        // Belt-and-suspenders fail-open: a retrieval throw degrades to an un-grounded (history-only) turn, never a fault.
        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        _capturedGrounding.Should().BeEmpty();
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldSkipGrounding_WhenSpendHasCrossedTheAlertThreshold()
    {
        // Spend is over the 50% alert threshold (6 of 10) but under the hard cap: the chat call still runs, but
        // grounding (an extra embed + rerank) is shed before the cap would block the turn.
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 6m, at: At(1));
        TurnReturnsCapturingGrounding(new ChatTurnResult(true, "ok", [Cost()]));

        IResult result = await Invoke(
            id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m, AlertThresholdFraction = 0.5m });

        StatusOf(result).Should().Be(StatusCodes.Status200OK); // not blocked -- the chat call itself proceeds
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustNotHaveHappened();
        _capturedGrounding.Should().BeEmpty();
    }

    [Fact]
    public async Task TurnAsync_ShouldNotRetrieve_WhenTheGovernorBlocksTheTurn()
    {
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 25m, at: At(1)); // over the 10 USD cap

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status429TooManyRequests);
        // A 429-blocked turn returns before persist -- it never reaches retrieval (no second gate, no wasted spend).
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldGround_WhenSpendIsBelowTheAlertThreshold()
    {
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 2m, at: At(1)); // under the 80% threshold of the 10 USD cap
        RetrieverReturns(Item());
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // --- One in-flight turn per conversation (gh#1106) ---

    [Fact]
    public async Task TurnAsync_ShouldRunTheWholeTurn_UnderTheConversationsTurnGuard()
    {
        // The guard has to wrap the WHOLE turn, not just the model call: the operator's turn is persisted before the
        // call and the assistant's after it, so a guard that only wrapped StreamAsync would let a refused second turn
        // still write into the thread. The keying is the CONVERSATION -- that is the correlation key the chunk stream
        // has, so it is the granularity the guarantee has to be stated at.
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));
        IChatTurnGuard guard = FreeGuard();

        IResult result = await Invoke(id, "hi", At(3), guard: guard);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => guard.TryRunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, id, A<Func<Task<IResult>>>._, A<Func<IResult>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldRefuseWithConflictAndPersistNothing_WhenATurnIsAlreadyInFlight()
    {
        Guid id = await SeedConversationAsync();

        IResult result = await Invoke(id, "and another thing", At(3), guard: BusyGuard());

        // 409, not 422: this endpoint already spends 422 on "the turn ran and could not produce an answer", and a
        // client that could not tell those apart would show the wrong affordance. 409 is what the endpoint already
        // means by "a concurrent request took this; retry" (the sequence race), which is exactly this case.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        string body = ValueOf<object>(result).ToString()!;
        body.Should().Contain("already in flight");
        // The refusal envelope's `layer` names the gate that said no, and here it is load-bearing rather than
        // decorative: this endpoint has a SECOND 409 (the lost sequence race) that demands the OPPOSITE client
        // behaviour around the live draft, and the status alone cannot separate them. Without this the client
        // cannot tell "my turn never ran, someone else's is streaming" from "my turn streamed and is over".
        body.Should().Contain(ChatEndpoints.TurnInFlightLayer);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .MustNotHaveHappened();

        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.AnyAsync()).Should().BeFalse(
            "a refused turn persists nothing at all -- the operator's turn is written INSIDE the guarded section");
    }

    [Fact]
    public async Task TurnAsync_ShouldRefuseTheSecondOfTwoGenuinelyConcurrentTurns_OnOneConversation()
    {
        // The race is the whole point of gh#1106, so this drives it rather than calling the endpoint twice in
        // sequence: the first turn is suspended INSIDE its guarded section (parked on the model call) at the moment
        // the second arrives, which is the interleave a naive check-then-act loses. No wall-clock sleep anywhere --
        // the handshake is two TaskCompletionSources, and every await is time-bounded so a regression fails the test
        // rather than hanging the CI run.
        Guid id = await SeedConversationAsync();
        SerializingChatTurnGuard guard = new();
        TaskCompletionSource firstIsInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                firstIsInside.TrySetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return new ChatTurnResult(true, "the first turn's answer", [Cost()]);
            });

        Task<IResult> first = Invoke(id, "first", At(3), guard: guard);
        await firstIsInside.Task.WaitAsync(TimeSpan.FromSeconds(30));
        IResult second = await Invoke(id, "second", At(4), guard: guard).WaitAsync(TimeSpan.FromSeconds(30));
        releaseFirst.TrySetResult();
        IResult firstResult = await first.WaitAsync(TimeSpan.FromSeconds(30));

        StatusOf(second).Should().Be(StatusCodes.Status409Conflict);
        StatusOf(firstResult).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext verify = Context();
        List<ChatMessage> thread = await verify.ChatMessages.OrderBy(message => message.Sequence).ToListAsync();
        thread.Select(message => message.Content).Should().Equal(
            ["first", "the first turn's answer"],
            "the refused turn contributed nothing -- not even the operator message it would have persisted first");
    }

    [Fact]
    public async Task TurnAsync_ShouldNotRunTheTurn_WhenTheGuardCannotBeTaken()
    {
        // Fail CLOSED. A guard that throws (the lock could not be evaluated) must not degrade into an un-guarded
        // turn: the request fails and nothing runs, rather than a possibly-concurrent turn reaching the model.
        Guid id = await SeedConversationAsync();
        IChatTurnGuard broken = A.Fake<IChatTurnGuard>();
        A.CallTo(() => broken.TryRunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<Func<IResult>>._,
                A<CancellationToken>._))
            .Throws(new InvalidOperationException("advisory lock unavailable"));

        Func<Task> act = () => Invoke(id, "hi", At(3), guard: broken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<IReadOnlyList<RetrievedContextItem>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        await using TradingCopilotDbContext verify = Context();
        (await verify.ChatMessages.AnyAsync()).Should().BeFalse();
    }

    // --- A faulted turn pushes a terminator (gh#1107) ---

    [Fact]
    public async Task TurnAsync_ShouldPushTheTurnFaultedTerminator_OnAFailedTurn()
    {
        // A faulted turn streams and then goes silent: the sender learns from the 422, and until this signal existed
        // every OTHER connection kept its half-written draft forever. The conversation id is a sufficient correlation
        // key BECAUSE of gh#1106 -- at most one turn is in flight on a conversation, so there is exactly one draft
        // for it to retire (which is why no turn id is on the wire).
        Guid id = await SeedConversationAsync();
        TurnStreams(new ChatTurnResult(false, "the co-pilot could not finish that turn", [Cost(AiUsageOutcome.Failed, 0m)]), "Half an ans");

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _notifier.TurnFaultedAsync(
                _operator,
                A<RealtimeChatTurnFaulted>.That.Matches(signal =>
                    signal.ConversationId == id && signal.Reason == "the co-pilot could not finish that turn"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.MessageAppendedAsync(A<Guid>._, A<RealtimeChatMessage>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldFallBackToAStatedReason_WhenTheFaultedTurnCarriesNoDisplayableMessage()
    {
        // A blank message is not a reason, and it must not become one anywhere: pushing "   " would render an empty
        // alert on the other screens, and returning it in the 422 renders an empty alert on the SENDER -- whose
        // client only checks that `error` is a string. So both carry the same stated fallback. Asserting the two
        // TOGETHER is the point: normalizing one path and not the other is exactly the bug this pins.
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(false, "   ", [Cost(AiUsageOutcome.Failed, 0m)]));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        ValueOf<object>(result).ToString().Should().Contain(ChatEndpoints.FaultedTurnFallbackReason);
        A.CallTo(() => _notifier.TurnFaultedAsync(
                _operator,
                A<RealtimeChatTurnFaulted>.That.Matches(signal => signal.Reason == ChatEndpoints.FaultedTurnFallbackReason),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldReturnAndPushTheSameReason_WhenTheFaultedTurnStatesOne()
    {
        // The sender and every other screen must read the same explanation for the same fault -- two screens of one
        // desk disagreeing about why an answer stopped is its own dishonesty (R-19).
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(false, "the model refused that request", [Cost(AiUsageOutcome.Failed, 0m)]));

        IResult result = await Invoke(id, "hi", At(3));

        ValueOf<object>(result).ToString().Should().Contain("the model refused that request");
        A.CallTo(() => _notifier.TurnFaultedAsync(
                _operator,
                A<RealtimeChatTurnFaulted>.That.Matches(signal => signal.Reason == "the model refused that request"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldStillReturnTheFault_WhenTheTurnFaultedPushThrows()
    {
        // Fail-OPEN, exactly like the chunk and message pushes: a hub fault never changes the turn's outcome or the
        // HTTP response. The hub is presentation-only (ADR-0021); the REST response is the source of truth.
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(false, "could not answer", [Cost(AiUsageOutcome.Failed, 0m)]));
        A.CallTo(() => _notifier.TurnFaultedAsync(A<Guid>._, A<RealtimeChatTurnFaulted>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("hub down"));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        ValueOf<object>(result).ToString().Should().Contain("could not answer");
        // Bind the fault to the push that threw it. Without this the test would pass just as happily against a
        // regression that removed the push altogether -- fail-open would be "proven" by nothing having been tried.
        A.CallTo(() => _notifier.TurnFaultedAsync(A<Guid>._, A<RealtimeChatTurnFaulted>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TurnAsync_ShouldPushNoTurnFaultedTerminator_OnASuccessfulTurn()
    {
        // The settled message push is a successful turn's terminator; a faulted signal alongside it would retire the
        // draft with an error affordance the turn does not deserve.
        Guid id = await SeedConversationAsync();
        TurnReturns(new ChatTurnResult(true, "ok", [Cost()]));

        IResult result = await Invoke(id, "hi", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _notifier.TurnFaultedAsync(A<Guid>._, A<RealtimeChatTurnFaulted>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldPushNoTurnFaultedTerminator_WhenTheTurnNeverRan()
    {
        // A governor block returns BEFORE anything streams, so no draft was ever opened on any connection. Pushing a
        // terminator for a turn that never started would retire the draft of whatever ran before it.
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 25m, at: At(1));

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status429TooManyRequests);
        A.CallTo(() => _notifier.TurnFaultedAsync(A<Guid>._, A<RealtimeChatTurnFaulted>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
