using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The chat persistence foundation (gh#18 increment 1, R-6): a <see cref="Conversation"/> and its ordered
/// <see cref="ChatMessage"/>s, both operator-owned (R-20). The behaviours that matter: a conversation round-trips
/// with its messages in order, and both the conversation <b>and</b> its messages are invisible to a different
/// operator — a message carries its own <c>UserId</c>, so a direct <c>ChatMessages</c> query is scoped too, not
/// only reads that join through the conversation.
/// </summary>
public class ChatPersistenceTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext ContextFor(Guid userId, string databaseName)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(userId));
    }

    private static DateTimeOffset At(int minute) => new(2026, 8, 15, 12, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task AConversationWithMessages_RoundTripsForItsOwner_MessagesInSequenceOrder()
    {
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid conversationId = Guid.NewGuid();

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            asOwner.Conversations.Add(new Conversation
            {
                Id = conversationId,
                UserId = owner,
                Title = "ES setup",
                CreatedAt = At(0),
                UpdatedAt = At(2),
            });
            asOwner.ChatMessages.AddRange(
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    UserId = owner,
                    ConversationId = conversationId,
                    Sequence = 1,
                    Role = ChatRole.User,
                    Content = "What's the ES setup?",
                    CreatedAt = At(0),
                },
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    UserId = owner,
                    ConversationId = conversationId,
                    Sequence = 2,
                    Role = ChatRole.Assistant,
                    Content = "A long entry above the pivot.",
                    CreatedAt = At(2),
                });
            await asOwner.SaveChangesAsync();
        }

        await using (TradingCopilotDbContext asOwner = ContextFor(owner, database))
        {
            Conversation loaded = await asOwner.Conversations.SingleAsync();
            loaded.Title.Should().Be("ES setup");

            List<ChatMessage> messages = await asOwner.ChatMessages
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.Sequence)
                .ToListAsync();

            messages.Select(m => m.Role).Should().ContainInOrder(ChatRole.User, ChatRole.Assistant);
            messages[0].Content.Should().Be("What's the ES setup?");
        }
    }

    [Fact]
    public async Task AConversationAndItsMessages_AreInvisibleToADifferentOperator()
    {
        Guid operatorA = Guid.NewGuid();
        Guid operatorB = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid conversationId = Guid.NewGuid();

        await using (TradingCopilotDbContext asA = ContextFor(operatorA, database))
        {
            asA.Conversations.Add(new Conversation
            {
                Id = conversationId,
                UserId = operatorA,
                Title = "private",
                CreatedAt = At(0),
                UpdatedAt = At(0),
            });
            asA.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                UserId = operatorA,
                ConversationId = conversationId,
                Sequence = 1,
                Role = ChatRole.User,
                Content = "secret",
                CreatedAt = At(0),
            });
            await asA.SaveChangesAsync();
        }

        // Default-deny: B sees neither the conversation nor — the belt-and-suspenders — its messages by a direct query.
        await using (TradingCopilotDbContext asB = ContextFor(operatorB, database))
        {
            (await asB.Conversations.AnyAsync()).Should().BeFalse();
            (await asB.ChatMessages.AnyAsync()).Should().BeFalse();
        }

        // ...and the owner still sees their own, so the filter scopes rather than simply hides everything.
        await using (TradingCopilotDbContext asA = ContextFor(operatorA, database))
        {
            (await asA.Conversations.ToListAsync()).Should().ContainSingle(c => c.Title == "private");
            (await asA.ChatMessages.ToListAsync()).Should().ContainSingle(m => m.Content == "secret");
        }
    }
}
