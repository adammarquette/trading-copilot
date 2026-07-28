using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Reading a pre-computed indicator value (R-22, gh#310) through <see cref="StoredIndicatorSource"/> — the seam
/// the safety-critical stop-promotion watcher consults for its ATR band.
/// </summary>
/// <remarks>
/// This suite did not exist before the framework increment: the concrete read seam was only exercised indirectly
/// through a fake in the promotion tests. It matters because <b>a missing value must read as absent, never as a
/// default</b> — a fallback distance is exactly the silent mis-measurement the ATR refusal was written to prevent.
/// </remarks>
public class StoredIndicatorSourceTests
{
    private const int AtrPeriod = 14;

    private static DateTimeOffset Bucket(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static StoredIndicatorSource Source(TradingCopilotDbContext context) =>
        new(context, Options.Create(new IndicatorOptions { AtrPeriod = AtrPeriod }));

    private static IndicatorValueRecord Row(string indicator, int period, int minute, decimal value) => new()
    {
        Venue = "projectx",
        Instrument = "ES",
        ResolutionMinutes = 1,
        Indicator = indicator,
        Period = period,
        BucketStart = Bucket(minute),
        Value = value,
        RecordedAt = Bucket(minute),
    };

    private static InstrumentId Es => InstrumentId.Parse("ES");

    // --- GetValueAsync: latest at or before, and absent means null ---

    [Fact]
    public async Task GetValueAsync_ShouldReturnTheMostRecentValue_AtOrBeforeTheMoment()
    {
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.AddRange(Row("rsi", 3, 1, 40m), Row("rsi", 3, 2, 55m), Row("rsi", 3, 3, 70m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetValueAsync(Es, "rsi", period: 3, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().Be(55m);
    }

    [Fact]
    public async Task GetValueAsync_ShouldIgnoreValuesAfterTheMoment()
    {
        // Asking what RSI was when a decision was taken must never return a number computed afterwards.
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.AddRange(Row("rsi", 3, 2, 55m), Row("rsi", 3, 5, 90m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetValueAsync(Es, "rsi", period: 3, resolutionMinutes: 1, asOf: Bucket(3), CancellationToken.None);

        value.Should().Be(55m);
    }

    [Fact]
    public async Task GetValueAsync_ShouldReturnNull_WhenNoValueExists()
    {
        // No default is substituted: absent is absent.
        await using TradingCopilotDbContext context = Context();

        decimal? value = await Source(context)
            .GetValueAsync(Es, "rsi", period: 3, resolutionMinutes: 1, asOf: Bucket(9), CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task GetValueAsync_ShouldKeepIndicatorsApart_ByName()
    {
        // An ATR must never answer for an RSI: they are different measures stored under the same instrument.
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.AddRange(Row("atr", 3, 2, 12m), Row("rsi", 3, 2, 55m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetValueAsync(Es, "rsi", period: 3, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().Be(55m);
    }

    [Fact]
    public async Task GetValueAsync_ShouldKeepPeriodsApart()
    {
        // An RSI(3) and an RSI(9) are different numbers; a consumer asking for one must not be handed the other.
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.AddRange(Row("rsi", 3, 2, 55m), Row("rsi", 9, 2, 48m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetValueAsync(Es, "rsi", period: 9, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().Be(48m);
    }

    // --- GetAverageTrueRangeAsync: the safety read, unchanged through delegation ---

    [Fact]
    public async Task GetAverageTrueRangeAsync_ShouldReturnTheStoredAtrAtTheConfiguredPeriod()
    {
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.Add(Row("atr", AtrPeriod, 2, 12.5m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetAverageTrueRangeAsync(Es, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().Be(12.5m);
    }

    [Fact]
    public async Task GetAverageTrueRangeAsync_ShouldReturnNull_WhenNoAtrCanBeMeasured()
    {
        // The safety pin: no value means "do not promote", never a fallback distance.
        await using TradingCopilotDbContext context = Context();

        decimal? value = await Source(context)
            .GetAverageTrueRangeAsync(Es, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task GetAverageTrueRangeAsync_ShouldReadTheConfiguredAtrPeriod_NotAnother()
    {
        // Only an atr row at AtrPeriod exists at another period -- the ATR read must not pick it up.
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.Add(Row("atr", AtrPeriod + 1, 2, 99m));
        await context.SaveChangesAsync();

        decimal? value = await Source(context)
            .GetAverageTrueRangeAsync(Es, resolutionMinutes: 1, asOf: Bucket(2), CancellationToken.None);

        value.Should().BeNull();
    }
}
