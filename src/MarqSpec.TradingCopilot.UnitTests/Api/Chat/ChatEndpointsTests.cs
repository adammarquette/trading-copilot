using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat;

/// <summary>
/// The <c>/conversations</c> chat CRUD (gh#18 inc 2, R-6). The behaviours that matter carry the two #900 review
/// guards: a message's owner is the <b>conversation's</b>, never request input (the request has no UserId field),
/// and appends allocate a monotonic <c>Sequence</c>; plus the R-20 rule that a foreign conversation is a 404.
/// </summary>
public class ChatEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private static DateTimeOffset At(int minute) => new(2026, 8, 15, 12, minute, 0, TimeSpan.Zero);

    private async Task<Guid> SeedConversationAsync(Guid? owner = null)
    {
        Guid id = Guid.NewGuid();
        Guid u = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(u);
        context.Conversations.Add(new Conversation { Id = id, UserId = u, Title = "seed", CreatedAt = At(0), UpdatedAt = At(0) });
        await context.SaveChangesAsync();
        return id;
    }

    // -- create ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ShouldStampTheCallerAsOwner_AndReturnTheConversation()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await ChatEndpoints.CreateAsync(
            new CreateConversationRequest("ES setup"), At(1), new FixedUser(_operator), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ConversationResponse response = ValueOf<ConversationResponse>(result);
        response.Title.Should().Be("ES setup");
        response.CreatedAt.Should().Be(At(1));
        response.UpdatedAt.Should().Be(At(1));

        await using TradingCopilotDbContext verify = Context();
        Conversation stored = await verify.Conversations.SingleAsync();
        stored.UserId.Should().Be(_operator);
    }

    [Fact]
    public async Task CreateAsync_ShouldAllowNoTitle()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await ChatEndpoints.CreateAsync(new CreateConversationRequest(null), At(1), new FixedUser(_operator), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ValueOf<ConversationResponse>(result).Title.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldReject_WhenTitleExceedsTheCap()
    {
        await using TradingCopilotDbContext context = Context();
        string tooLong = new('x', Conversation.TitleMaxLength + 1);

        IResult result = await ChatEndpoints.CreateAsync(new CreateConversationRequest(tooLong), At(1), new FixedUser(_operator), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // -- list ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ShouldReturnTheOperatorsConversations_MostRecentFirst()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.Conversations.Add(new Conversation { Id = Guid.NewGuid(), UserId = _operator, Title = "older", CreatedAt = At(0), UpdatedAt = At(1) });
            seed.Conversations.Add(new Conversation { Id = Guid.NewGuid(), UserId = _operator, Title = "newer", CreatedAt = At(0), UpdatedAt = At(5) });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.ListAsync(null, context, default);

        ConversationListResponse list = ValueOf<ConversationListResponse>(result);
        list.Conversations.Select(c => c.Title).Should().ContainInOrder("newer", "older");
    }

    [Fact]
    public async Task ListAsync_ShouldNotReturnAnotherOperatorsConversations()
    {
        await SeedConversationAsync(owner: Guid.NewGuid()); // a stranger's

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.ListAsync(null, context, default);

        ValueOf<ConversationListResponse>(result).Conversations.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ShouldReject_WhenLimitIsNotPositive()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await ChatEndpoints.ListAsync(0, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // -- get -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ShouldReturnTheConversationWithItsMessages_InSequenceOrder()
    {
        Guid conversationId = await SeedConversationAsync();
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = _operator, ConversationId = conversationId, Sequence = 2, Role = ChatRole.Assistant, Content = "second", CreatedAt = At(2) });
            seed.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = _operator, ConversationId = conversationId, Sequence = 1, Role = ChatRole.User, Content = "first", CreatedAt = At(1) });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.GetAsync(conversationId, context, default);

        ConversationDetailResponse detail = ValueOf<ConversationDetailResponse>(result);
        detail.Messages.Select(m => m.Content).Should().ContainInOrder("first", "second");
        detail.Messages.Select(m => m.Sequence).Should().ContainInOrder(1, 2);
    }

    [Fact]
    public async Task GetAsync_ShouldReturn404_ForAForeignConversation()
    {
        Guid foreign = await SeedConversationAsync(owner: Guid.NewGuid());

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.GetAsync(foreign, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // -- append ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AppendAsync_ShouldStampTheConversationsOwner_AndAllocateTheNextSequence_AndBumpUpdatedAt()
    {
        Guid conversationId = await SeedConversationAsync();

        await using (TradingCopilotDbContext context = Context())
        {
            IResult first = await ChatEndpoints.AppendAsync(
                conversationId, new AppendMessageRequest(ChatRole.User, "hello"), At(3), context, default);
            StatusOf(first).Should().Be(StatusCodes.Status200OK);
            ValueOf<ChatMessageResponse>(first).Sequence.Should().Be(1);
        }

        await using (TradingCopilotDbContext context = Context())
        {
            IResult second = await ChatEndpoints.AppendAsync(
                conversationId, new AppendMessageRequest(ChatRole.Assistant, "hi"), At(4), context, default);
            ValueOf<ChatMessageResponse>(second).Sequence.Should().Be(2);
        }

        await using TradingCopilotDbContext verify = Context();
        ChatMessage stored = await verify.ChatMessages.OrderBy(m => m.Sequence).FirstAsync();
        stored.UserId.Should().Be(_operator); // the conversation's owner, not request input
        (await verify.Conversations.SingleAsync()).UpdatedAt.Should().Be(At(4)); // bumped to the last append
    }

    [Fact]
    public async Task AppendAsync_ShouldReturn404_WhenTheConversationBelongsToAnotherOperator()
    {
        Guid foreign = await SeedConversationAsync(owner: Guid.NewGuid());

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.AppendAsync(
            foreign, new AppendMessageRequest(ChatRole.User, "sneak"), At(3), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);

        // and nothing was written to the foreign conversation
        await using TradingCopilotDbContext asStranger = Context(user: (await StrangerOwnerOf(foreign)));
        (await asStranger.ChatMessages.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_ShouldReject_WhenRoleIsUnknown()
    {
        Guid conversationId = await SeedConversationAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.AppendAsync(
            conversationId, new AppendMessageRequest(ChatRole.Unknown, "x"), At(3), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AppendAsync_ShouldReject_WhenContentIsBlank()
    {
        Guid conversationId = await SeedConversationAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await ChatEndpoints.AppendAsync(
            conversationId, new AppendMessageRequest(ChatRole.User, "   "), At(3), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    private async Task<Guid> StrangerOwnerOf(Guid conversationId)
    {
        // In these tests a foreign conversation was seeded by a random owner; recover it to assert as that owner.
        await using TradingCopilotDbContext raw = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));
        Conversation c = await raw.Conversations.IgnoreQueryFilters().SingleAsync(x => x.Id == conversationId);
        return c.UserId;
    }
}
