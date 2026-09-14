using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Journal;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Journal;

/// <summary>
/// The trade-feedback read (gh#1064, R-8) — the behaviours that matter: it returns one trade's feedback oldest
/// first, never another trade's or another operator's (R-20), and empty (not an error) when nothing has been
/// recorded yet.
/// </summary>
public class TradeFeedbackReaderTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _trade = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private async Task SeedAsync(Guid trade, DateTimeOffset createdAt, Guid? owner = null, string? comment = "noted")
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.TradeFeedbacks.Add(new TradeFeedback
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            TradeId = trade,
            Comment = comment,
            Author = FeedbackAuthor.Operator,
            CreatedAt = createdAt,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task FeedbackForTradeAsync_ShouldReturnEmpty_WhenNothingHasBeenRecorded()
    {
        await using TradingCopilotDbContext context = Context();

        IReadOnlyList<TradeFeedback> result = await context.FeedbackForTradeAsync(_trade, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FeedbackForTradeAsync_ShouldReturnEntries_OldestFirst()
    {
        DateTimeOffset first = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        await SeedAsync(_trade, first.AddMinutes(10), comment: "second");
        await SeedAsync(_trade, first, comment: "first");

        await using TradingCopilotDbContext context = Context();
        IReadOnlyList<TradeFeedback> result = await context.FeedbackForTradeAsync(_trade, CancellationToken.None);

        result.Select(entry => entry.Comment).Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task FeedbackForTradeAsync_ShouldNotReturnAnotherTradesFeedback()
    {
        await SeedAsync(_trade, DateTimeOffset.UtcNow, comment: "mine");
        await SeedAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, comment: "someone else's trade");

        await using TradingCopilotDbContext context = Context();
        IReadOnlyList<TradeFeedback> result = await context.FeedbackForTradeAsync(_trade, CancellationToken.None);

        result.Should().ContainSingle().Which.Comment.Should().Be("mine");
    }

    [Fact]
    public async Task FeedbackForTradeAsync_ShouldNotReturnAnotherOperatorsFeedback()
    {
        await SeedAsync(_trade, DateTimeOffset.UtcNow, owner: Guid.NewGuid()); // a stranger's feedback

        await using TradingCopilotDbContext context = Context(); // the caller
        IReadOnlyList<TradeFeedback> result = await context.FeedbackForTradeAsync(_trade, CancellationToken.None);

        result.Should().BeEmpty(); // R-20 default-deny
    }
}
