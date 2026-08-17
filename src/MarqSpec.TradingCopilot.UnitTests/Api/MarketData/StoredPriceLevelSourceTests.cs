using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The key-level read surface (gh#596, R-10): <see cref="StoredPriceLevelSource"/> returns the <b>active</b> levels
/// for an instrument across a requested set of timeframes, strongest first, and — being <b>global</b> market data
/// (R-20, not <c>IUserOwned</c>) — is visible to every operator. Retired levels, other instruments/venues, and
/// unrequested timeframes are excluded.
/// </summary>
public class StoredPriceLevelSourceTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly DateTimeOffset _t = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // PriceLevel is global (not operator-owned), so the user is irrelevant — a fresh one per context proves it.
    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static PriceLevel Level(
        string venue = "projectx",
        string instrument = "ES",
        int timeframe = 60,
        decimal bottom = 5000m,
        decimal top = 5010m,
        PriceLevelKind kind = PriceLevelKind.Support,
        decimal significance = 1m,
        bool active = true,
        int touchCount = 1) =>
        new()
        {
            Id = Guid.NewGuid(),
            Venue = venue,
            Instrument = instrument,
            TimeframeMinutes = timeframe,
            Bottom = bottom,
            Top = top,
            Kind = kind,
            Significance = significance,
            FormedAtBucket = _t,
            TouchCount = touchCount,
            Active = active,
            UpdatedAt = _t,
        };

    private async Task SeedAsync(params PriceLevel[] levels)
    {
        await using TradingCopilotDbContext context = Context();
        context.PriceLevels.AddRange(levels);
        await context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<PriceLevel>> GetAsync(
        string venue = "projectx", string instrument = "ES", params int[] timeframes)
    {
        await using TradingCopilotDbContext context = Context();
        return await new StoredPriceLevelSource(context).GetActiveLevelsAsync(venue, instrument, timeframes, default);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldReturnActiveLevels_ForTheInstrumentAndTimeframe()
    {
        PriceLevel a = Level(significance: 2m);
        PriceLevel b = Level(significance: 1m);
        await SeedAsync(a, b);

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: 60);

        levels.Select(level => level.Id).Should().BeEquivalentTo([a.Id, b.Id]);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldExcludeRetiredLevels()
    {
        PriceLevel live = Level(active: true);
        await SeedAsync(live, Level(active: false));

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(live.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldExcludeOtherInstruments()
    {
        PriceLevel mine = Level(instrument: "ES");
        await SeedAsync(mine, Level(instrument: "NQ"));

        IReadOnlyList<PriceLevel> levels = await GetAsync(instrument: "ES", timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldExcludeOtherVenues()
    {
        PriceLevel mine = Level(venue: "projectx");
        await SeedAsync(mine, Level(venue: "finnhub"));

        IReadOnlyList<PriceLevel> levels = await GetAsync(venue: "projectx", timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldReturnOnlyTheRequestedTimeframes()
    {
        PriceLevel hourly = Level(timeframe: 60);
        await SeedAsync(hourly, Level(timeframe: 5));

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(hourly.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldReturnAcrossMultipleTimeframes_WhenSeveralAreRequested()
    {
        PriceLevel hourly = Level(timeframe: 60);
        PriceLevel fiveMin = Level(timeframe: 5);
        await SeedAsync(hourly, fiveMin, Level(timeframe: 15));

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: [5, 60]);

        levels.Select(level => level.Id).Should().BeEquivalentTo([hourly.Id, fiveMin.Id]);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldOrderByTimeframeThenSignificanceDescending()
    {
        PriceLevel hourlyStrong = Level(timeframe: 60, significance: 5m);
        PriceLevel hourlyWeak = Level(timeframe: 60, significance: 1m);
        PriceLevel fiveMin = Level(timeframe: 5, significance: 9m);
        await SeedAsync(hourlyWeak, fiveMin, hourlyStrong);

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: [5, 60]);

        // Timeframe ascending, then strongest first within a timeframe.
        levels.Select(level => level.Id).Should().Equal(fiveMin.Id, hourlyStrong.Id, hourlyWeak.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldBeVisibleToAnyOperator_BecauseLevelsAreGlobal()
    {
        // Seeded under one user, read under another (each Context() mints a fresh user): a tenant filter here would
        // be a defect — it would hide the market from the operator trading it (R-20 global market data).
        PriceLevel level = Level();
        await SeedAsync(level);

        IReadOnlyList<PriceLevel> levels = await GetAsync(timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(level.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldReturnEmpty_ForAnEmptyTimeframeSet()
    {
        await SeedAsync(Level());

        (await GetAsync(timeframes: [])).Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldReturnEmpty_WhenNothingMatches()
    {
        await SeedAsync(Level(instrument: "NQ"));

        (await GetAsync(instrument: "ES", timeframes: 60)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetActiveLevelsAsync_ShouldRejectABlankInstrument(string instrument)
    {
        await using TradingCopilotDbContext context = Context();
        Func<Task> act = () => new StoredPriceLevelSource(context).GetActiveLevelsAsync("projectx", instrument, [60], default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetActiveLevelsAsync_ShouldRejectANullTimeframeSet()
    {
        await using TradingCopilotDbContext context = Context();
        Func<Task> act = () => new StoredPriceLevelSource(context).GetActiveLevelsAsync("projectx", "ES", null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- The venue-agnostic overload (gh#730): the SAME rules, but NO venue filter -- it reads every venue's active
    //      levels for the instrument, because the confluence scan holds only the venue-neutral symbol (gate-below-model,
    //      mirroring StoredIndicatorSource). Its own public method, so it carries its own coverage. ----

    private async Task<IReadOnlyList<PriceLevel>> GetAnyVenueAsync(
        string instrument = "ES", params int[] timeframes)
    {
        await using TradingCopilotDbContext context = Context();
        return await new StoredPriceLevelSource(context).GetActiveLevelsAsync(instrument, timeframes, default);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_VenueAgnostic_ShouldReturnLevelsAcrossEveryVenue()
    {
        // The distinguishing behavior: unlike the venue-taking overload, this returns the instrument's active levels
        // from ALL venues at once. A venue filter here would be the defect (the scan has no venue to filter on).
        PriceLevel onProjectX = Level(venue: "projectx", timeframe: 60);
        PriceLevel onFinnhub = Level(venue: "finnhub", timeframe: 60);
        await SeedAsync(onProjectX, onFinnhub);

        IReadOnlyList<PriceLevel> levels = await GetAnyVenueAsync(instrument: "ES", timeframes: 60);

        levels.Select(level => level.Id).Should().BeEquivalentTo([onProjectX.Id, onFinnhub.Id],
            "the venue-agnostic overload reads every venue's active levels for the symbol");
    }

    [Fact]
    public async Task GetActiveLevelsAsync_VenueAgnostic_ShouldStillExcludeRetiredAndUnrequestedTimeframes()
    {
        PriceLevel wanted = Level(venue: "projectx", timeframe: 60, active: true);
        await SeedAsync(
            wanted,
            Level(venue: "finnhub", timeframe: 60, active: false), // retired -> excluded even across venues
            Level(venue: "finnhub", timeframe: 5, active: true));  // unrequested timeframe -> excluded

        IReadOnlyList<PriceLevel> levels = await GetAnyVenueAsync(instrument: "ES", timeframes: 60);

        levels.Should().ContainSingle().Which.Id.Should().Be(wanted.Id);
    }

    [Fact]
    public async Task GetActiveLevelsAsync_VenueAgnostic_ShouldReturnEmpty_ForAnEmptyTimeframeSet()
    {
        await SeedAsync(Level());

        (await GetAnyVenueAsync(instrument: "ES", timeframes: [])).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetActiveLevelsAsync_VenueAgnostic_ShouldRejectABlankInstrument(string instrument)
    {
        await using TradingCopilotDbContext context = Context();
        Func<Task> act = () => new StoredPriceLevelSource(context).GetActiveLevelsAsync(instrument, [60], default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetActiveLevelsAsync_VenueAgnostic_ShouldRejectANullTimeframeSet()
    {
        await using TradingCopilotDbContext context = Context();
        Func<Task> act = () => new StoredPriceLevelSource(context).GetActiveLevelsAsync("ES", null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
