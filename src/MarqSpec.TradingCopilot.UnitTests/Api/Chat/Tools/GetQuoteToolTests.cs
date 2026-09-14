using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>get_quote</c> chat tool (gh#925): a read-only read of the most recent stored bar. <c>BarRecord</c> is fully
/// mapped in-memory and <b>global</b> (not owner-scoped), so the latest-bar read, the resolution filter, and the
/// fail-closed input handling are unit-testable behind an in-memory <see cref="TradingCopilotDbContext"/>.
/// </summary>
public class GetQuoteToolTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options, new FixedUser(Guid.NewGuid()));

    private GetQuoteTool Tool() => new(Context(), NullLogger<GetQuoteTool>.Instance);

    private static string Key(string symbol)
    {
        InstrumentId.TryParse(symbol, out InstrumentId id);
        return id.ToString();
    }

    private static BarRecord Bar(string instrument, int resolution, int minute, decimal close) => new()
    {
        Venue = "topstepx",
        Instrument = Key(instrument),
        ResolutionMinutes = resolution,
        BucketStart = _now.AddMinutes(minute),
        Open = close - 2m,
        High = close + 3m,
        Low = close - 4m,
        Close = close,
        Volume = 1000,
        RecordedAt = _now.AddMinutes(minute),
    };

    private async Task SeedAsync(params BarRecord[] bars)
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.AddRange(bars);
        await context.SaveChangesAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTheMostRecentBar_ForTheInstrument()
    {
        await SeedAsync(Bar("ES", 1, 1, 5000m), Bar("ES", 1, 5, 5010m));

        string result = await Tool().ExecuteAsync("{\"instrument\":\"ES\"}", CancellationToken.None);

        JsonElement quote = Parse(result).GetProperty("quote");
        quote.GetProperty("close").GetDecimal().Should().Be(5010m); // newest BucketStart
        quote.GetProperty("instrument").GetString().Should().Be(Key("ES"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFilterByResolution_WhenGiven()
    {
        await SeedAsync(Bar("ES", 1, 9, 5001m), Bar("ES", 5, 5, 5055m));

        string result = await Tool().ExecuteAsync("{\"instrument\":\"ES\",\"resolution\":5}", CancellationToken.None);

        JsonElement quote = Parse(result).GetProperty("quote");
        quote.GetProperty("resolutionMinutes").GetInt32().Should().Be(5);
        quote.GetProperty("close").GetDecimal().Should().Be(5055m);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnAnError_WhenTheInstrumentHasNoData()
    {
        await SeedAsync(Bar("ES", 1, 1, 5000m));

        string result = await Tool().ExecuteAsync("{\"instrument\":\"NQ\"}", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInputIsMalformedOrMissingInstrument()
    {
        Parse(await Tool().ExecuteAsync("not json", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        Parse(await Tool().ExecuteAsync("{}", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Definition_ShouldBeNamedGetQuote_AndRequireInstrument()
    {
        IChatTool tool = Tool();

        tool.Name.Should().Be("get_quote");
        JsonElement schema = Parse(tool.Definition.InputSchema);
        schema.GetProperty("required")[0].GetString().Should().Be("instrument");
    }
}
