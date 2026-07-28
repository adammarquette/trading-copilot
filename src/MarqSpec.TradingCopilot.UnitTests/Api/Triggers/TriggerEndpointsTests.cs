using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The <c>/api/triggers</c> handlers (gh#385). Safety-relevant behaviours: a malformed condition is refused whole
/// (the refusable-zero comparison), the deferred agent-review route is refused (422), an edit re-seeds the
/// debounce so it cannot fire on a now-already-true condition, and everything is operator-owned (R-20).
/// </summary>
public class TriggerEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;
        return new TradingCopilotDbContext(options, new FixedUser(asUser ?? _operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static TriggerRequest ValidRequest(
        TriggerRoute route = TriggerRoute.MechanicalAlert,
        ThresholdComparison comparison = ThresholdComparison.Below,
        string instrument = "ES") => new(instrument, "rsi", 14, 1, comparison, 30m, route, Enabled: true);

    private async Task<Guid> SeedTriggerAsync(Guid owner, TriggerArmState state = TriggerArmState.Fired, int armCycle = 2)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext database = Context(owner);
        database.Triggers.Add(new Trigger
        {
            Id = id,
            UserId = owner,
            Instrument = "ES",
            Indicator = "rsi",
            Period = 14,
            ResolutionMinutes = 1,
            Comparison = ThresholdComparison.Below,
            Threshold = 30m,
            Route = TriggerRoute.MechanicalAlert,
            Enabled = true,
            ArmState = state,
            ArmCycle = armCycle,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await database.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Create_ShouldPersistOwnedAndUnseeded()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(), new FixedUser(_operator), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);

        await using TradingCopilotDbContext read = Context();
        Trigger stored = await read.Triggers.SingleAsync();
        stored.UserId.Should().Be(_operator);
        stored.ArmState.Should().Be(TriggerArmState.Unseeded);
        stored.Indicator.Should().Be("rsi");
    }

    [Fact]
    public async Task Create_ShouldRefuseTheUnknownComparison()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(comparison: ThresholdComparison.Unknown), new FixedUser(_operator), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await database.Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldRefuseTheAgentReviewRoute_AsNotYetAvailable()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(route: TriggerRoute.AgentReview), new FixedUser(_operator), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await database.Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldRefuseABlankInstrument()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(instrument: "   "), new FixedUser(_operator), database, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Get_ShouldNotFindAnotherOperatorsTrigger()
    {
        Guid id = await SeedTriggerAsync(_operator);

        await using TradingCopilotDbContext asStranger = Context(_stranger);
        IResult result = await TriggerEndpoints.GetTriggerAsync(id, asStranger, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Update_ShouldReSeedTheDebounceAndBumpTheCycle()
    {
        Guid id = await SeedTriggerAsync(_operator, TriggerArmState.Fired, armCycle: 2);

        await using (TradingCopilotDbContext database = Context())
        {
            IResult result = await TriggerEndpoints.UpdateTriggerAsync(
                id, ValidRequest(comparison: ThresholdComparison.Above), database, CancellationToken.None);
            StatusOf(result).Should().Be(StatusCodes.Status200OK);
        }

        await using TradingCopilotDbContext read = Context();
        Trigger stored = await read.Triggers.SingleAsync();
        stored.ArmState.Should().Be(TriggerArmState.Unseeded); // re-seeded
        stored.ArmCycle.Should().Be(3);                        // fresh incident namespace
        stored.Comparison.Should().Be(ThresholdComparison.Above);
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheTrigger_ButKeepItsFirings()
    {
        Guid id = await SeedTriggerAsync(_operator);
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TriggerFirings.Add(new TriggerFiringRecord
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                TriggerId = id,
                ArmCycle = 0,
                Instrument = "ES",
                Indicator = "rsi",
                Period = 14,
                ResolutionMinutes = 1,
                Comparison = ThresholdComparison.Below,
                Threshold = 30m,
                Value = 25m,
                FiredAt = DateTimeOffset.UnixEpoch,
            });
            await seed.SaveChangesAsync();
        }

        await using (TradingCopilotDbContext database = Context())
        {
            IResult result = await TriggerEndpoints.DeleteTriggerAsync(id, database, CancellationToken.None);
            StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        }

        await using TradingCopilotDbContext read = Context();
        (await read.Triggers.AnyAsync()).Should().BeFalse();
        (await read.TriggerFirings.CountAsync()).Should().Be(1); // the journal outlives the trigger
    }

    [Fact]
    public async Task List_ShouldReturnOnlyTheCallersTriggers()
    {
        await SeedTriggerAsync(_operator);
        await SeedTriggerAsync(_stranger);

        await using TradingCopilotDbContext asOperator = Context();
        IResult result = await TriggerEndpoints.ListTriggersAsync(asOperator, CancellationToken.None);

        var payload = (IValueHttpResult)result;
        ((IReadOnlyList<TriggerResponse>)payload.Value!).Should().ContainSingle();
    }

    [Fact]
    public async Task Create_ShouldRefuseAnOverLongInstrument()
    {
        await using TradingCopilotDbContext database = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(instrument: new string('X', 40)), new FixedUser(_operator), database, CancellationToken.None);

        // A clean 400, not a 500 from the varchar(32) column at SaveChanges.
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Update_ShouldNotFindAnotherOperatorsTrigger()
    {
        Guid id = await SeedTriggerAsync(_operator);

        await using TradingCopilotDbContext asStranger = Context(_stranger);
        IResult result = await TriggerEndpoints.UpdateTriggerAsync(id, ValidRequest(), asStranger, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Delete_ShouldNotFindAnotherOperatorsTrigger()
    {
        Guid id = await SeedTriggerAsync(_operator);

        await using TradingCopilotDbContext asStranger = Context(_stranger);
        IResult result = await TriggerEndpoints.DeleteTriggerAsync(id, asStranger, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }
}
