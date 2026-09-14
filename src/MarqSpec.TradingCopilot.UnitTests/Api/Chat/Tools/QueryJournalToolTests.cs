using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>query_journal</c> chat tool (gh#925): a read-only, owner-scoped read of the trader's recent closed trades.
/// The <c>Trade</c> entity is fully mapped in-memory (unlike a vector), so the read + its R-20 scoping + the
/// fail-closed input handling are all unit-testable behind an in-memory <see cref="TradingCopilotDbContext"/>.
/// </summary>
public class QueryJournalToolTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid asUser) => new(Options, new FixedUser(asUser));

    private QueryJournalTool Tool() => new(Context(_owner), NullLogger<QueryJournalTool>.Instance);

    private static Trade ClosedTrade(string instrument, decimal realizedPnL, int minute) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Instrument = instrument,
        Side = OrderSide.Buy,
        Size = 2,
        EntryPrice = 5000m,
        ExitPrice = 5000m + realizedPnL,
        RealizedPnL = realizedPnL,
        Mode = TradingMode.Practice,
        ClosedAt = _now.AddMinutes(minute),
    };

    private async Task SeedAsync(Guid owner, params Trade[] trades)
    {
        await using TradingCopilotDbContext context = Context(owner);
        foreach (Trade trade in trades)
        {
            trade.UserId = owner; // the owning operator (R-20) the tenant filter reads back against
        }

        context.Trades.AddRange(trades);
        await context.SaveChangesAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ExecuteAsync_ShouldReturnRecentClosedTrades_AsCompactJson()
    {
        await SeedAsync(_owner, ClosedTrade("MES", 125m, 1), ClosedTrade("MNQ", -40m, 2));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        JsonElement trades = Parse(result).GetProperty("trades");
        trades.GetArrayLength().Should().Be(2);
        result.Should().Contain("MES").And.Contain("MNQ").And.Contain("125").And.Contain("-40");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTheMostRecentFirst_AndRespectTheLimit()
    {
        await SeedAsync(_owner,
            ClosedTrade("A", 1m, 1), ClosedTrade("B", 2m, 2), ClosedTrade("C", 3m, 3));

        string result = await Tool().ExecuteAsync("{\"limit\":1}", CancellationToken.None);

        JsonElement trades = Parse(result).GetProperty("trades");
        trades.GetArrayLength().Should().Be(1);
        trades[0].GetProperty("instrument").GetString().Should().Be("C"); // newest ClosedAt
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnOnlyTheOwnersTrades()
    {
        Guid other = Guid.NewGuid();
        await SeedAsync(_owner, ClosedTrade("MINE", 10m, 1));
        await SeedAsync(other, ClosedTrade("THEIRS", 99m, 2));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        result.Should().Contain("MINE").And.NotContain("THEIRS");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInputIsMalformed()
    {
        await SeedAsync(_owner, ClosedTrade("MES", 10m, 1));

        // A malformed input returns a compact error result -- never a throw, and never a partial/invented read.
        string result = await Tool().ExecuteAsync("not json", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Definition_ShouldBeNamedQueryJournal_WithAnObjectSchema()
    {
        IChatTool tool = Tool();

        tool.Name.Should().Be("query_journal");
        tool.Definition.Name.Should().Be("query_journal");
        tool.Definition.Description.Should().NotBeNullOrWhiteSpace();
        Parse(tool.Definition.InputSchema).GetProperty("type").GetString().Should().Be("object");
    }
}
