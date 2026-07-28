using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Relevance;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Relevance;

/// <summary>
/// The <c>/api/relevance</c> handlers (gh#359) — the operator curates the global ticker maps and topics. Safety-
/// relevant behaviours: input is normalized + validated (refusable-zero scope, instrument-scoped needs an
/// instrument), and every mutation bumps the config-version marker so the resolution pass re-resolves.
/// </summary>
public class RelevanceEndpointsTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static TopicRequest ValidTopic(TopicScope scope = TopicScope.Global, string? instrument = null) =>
        new("fomc", ["FOMC", "rate decision"], scope, instrument);

    // --- Maps ---

    [Fact]
    public async Task CreateMap_ShouldNormalizePersistAndBumpConfig()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.CreateMapAsync(new TickerMapRequest("spy", "es"), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);

        await using TradingCopilotDbContext read = Context();
        TickerInstrumentMap stored = await read.TickerInstrumentMaps.SingleAsync();
        stored.Ticker.Should().Be("SPY"); // normalized upper-case
        stored.Instrument.Should().Be("ES");
        (await read.RelevanceConfigStates.AnyAsync()).Should().BeTrue(); // config bumped
    }

    [Fact]
    public async Task CreateMap_ShouldRefuseADuplicate()
    {
        await using TradingCopilotDbContext database = Context();
        await RelevanceEndpoints.CreateMapAsync(new TickerMapRequest("SPY", "ES"), database, CancellationToken.None);

        IResult result = await RelevanceEndpoints.CreateMapAsync(new TickerMapRequest("spy", "es"), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task DeleteMap_ShouldRemoveAndBump()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext database = Context();
        IResult result = await RelevanceEndpoints.DeleteMapAsync("spy", "es", database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        (await database.TickerInstrumentMaps.AnyAsync()).Should().BeFalse();
    }

    // --- Topics ---

    [Fact]
    public async Task CreateTopic_ShouldPersistAndBump()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.CreateTopicAsync(ValidTopic(), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        (await database.NewsTopics.SingleAsync()).Name.Should().Be("fomc");
        (await database.RelevanceConfigStates.AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTopic_ShouldRefuseTheUnknownScope()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.CreateTopicAsync(ValidTopic(TopicScope.Unknown), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await database.NewsTopics.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateTopic_ShouldRefuseInstrumentScopeWithoutAnInstrument()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.CreateTopicAsync(
            ValidTopic(TopicScope.Instrument, instrument: null), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateTopic_ShouldRefuseNoKeywords()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.CreateTopicAsync(
            new TopicRequest("fomc", [], TopicScope.Global, null), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateTopic_ShouldReplaceWholeAndBump()
    {
        Guid id;
        await using (TradingCopilotDbContext seed = Context())
        {
            id = Guid.NewGuid();
            seed.NewsTopics.Add(new NewsTopic
            {
                Id = id,
                Name = "fomc",
                Keywords = ["FOMC"],
                Scope = TopicScope.Global,
                Instrument = null,
            });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext database = Context();
        IResult result = await RelevanceEndpoints.UpdateTopicAsync(
            id, new TopicRequest("crude", ["oil"], TopicScope.Instrument, "CL"), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        NewsTopic stored = await database.NewsTopics.SingleAsync();
        stored.Name.Should().Be("crude");
        stored.Scope.Should().Be(TopicScope.Instrument);
        stored.Instrument.Should().Be("CL");
    }

    [Fact]
    public async Task GetTopic_ShouldReturnNotFound_ForAMissingId()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await RelevanceEndpoints.GetTopicAsync(Guid.NewGuid(), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DeleteTopic_ShouldRemove()
    {
        Guid id;
        await using (TradingCopilotDbContext seed = Context())
        {
            id = Guid.NewGuid();
            seed.NewsTopics.Add(new NewsTopic { Id = id, Name = "fomc", Keywords = ["FOMC"], Scope = TopicScope.Global });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext database = Context();
        IResult result = await RelevanceEndpoints.DeleteTopicAsync(id, database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        (await database.NewsTopics.AnyAsync()).Should().BeFalse();
    }
}
