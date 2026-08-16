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
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).Returns(result);

    // Makes the fake turn service stream the given deltas to the endpoint's onDelta (which pushes to the hub), then
    // return the result -- so a test can assert the endpoint forwarded each delta.
    private void TurnStreams(ChatTurnResult result, params string[] deltas) =>
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<ChatMessage> _, Func<string, CancellationToken, Task> onDelta, CancellationToken ct) =>
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

    private Task<IResult> Invoke(Guid id, string content, DateTimeOffset now, GovernorOptions? governor = null) =>
        ChatEndpoints.TurnAsync(
            id, new ChatTurnRequest(content), now, Context(),
            _turn, _governor, Options.Create(governor ?? new GovernorOptions()),
            _ledger, _metrics, _notifier, NullLoggerFactory.Instance, default);

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
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReject_WhenContentIsBlank()
    {
        Guid id = await SeedConversationAsync();

        IResult result = await Invoke(id, "   ", At(3));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReject_WhenContentExceedsTheCap()
    {
        Guid id = await SeedConversationAsync();
        string tooLong = new('x', ChatMessage.ContentMaxLength + 1);

        IResult result = await Invoke(id, tooLong, At(3));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TurnAsync_ShouldReturn429_AndMakeNoCall_AndPersistNothing_WhenTheDailyBudgetIsReached()
    {
        Guid id = await SeedConversationAsync();
        await SeedSpendAsync(usd: 25m, at: At(1)); // today's spend already over the 10 USD cap

        IResult result = await Invoke(id, "hi", At(3), governor: new GovernorOptions { DailyBudgetUsd = 10m });

        StatusOf(result).Should().Be(StatusCodes.Status429TooManyRequests);
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustNotHaveHappened();
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
        A.CallTo(() => _turn.StreamAsync(A<IReadOnlyList<ChatMessage>>._, A<Func<string, CancellationToken, Task>>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
}
