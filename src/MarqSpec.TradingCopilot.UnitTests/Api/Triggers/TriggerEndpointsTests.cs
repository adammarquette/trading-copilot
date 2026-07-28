using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The <c>/api/triggers</c> handlers (gh#385, R-4 / R-7) — the operator authors and manages standing triggers. The
/// safety-relevant behaviours: a create is validated whole at the boundary (bad enum, non-positive period /
/// resolution, unknown indicator, the not-yet-available agent-review route are all refused, nothing partially
/// stored); a create starts Enabled + Unseeded + cycle 0; an edit re-seeds the arm state without touching the
/// incident cycle; and every read/write is R-20-scoped to the caller.
/// </summary>
public class TriggerEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static CreateTriggerRequest ValidRequest() => new(
        Symbol: "ES",
        Indicator: "rsi",
        Period: 14,
        ResolutionMinutes: 1,
        Comparison: IndicatorComparison.Below,
        Threshold: 30m,
        Route: TriggerRoute.Mechanical);

    private async Task<Guid> SeedTriggerAsync(
        Guid owner,
        TriggerArmState armState = TriggerArmState.Armed,
        int armCycle = 0)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(owner);
        context.Triggers.Add(new TriggerRecord
        {
            Id = id,
            UserId = owner,
            Symbol = "ES",
            Indicator = "rsi",
            Period = 14,
            ResolutionMinutes = 1,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = IndicatorComparison.Below,
            Threshold = 30m,
            Route = TriggerRoute.Mechanical,
            Severity = NotificationSeverity.Notify,
            Enabled = true,
            ArmState = armState,
            ArmCycle = armCycle,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
        return id;
    }

    // --- Create ---

    [Fact]
    public async Task Create_ShouldPersistAnEnabledUnseededTrigger_OwnedByTheCaller()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        await using TradingCopilotDbContext reload = Context();
        TriggerRecord stored = await reload.Triggers.SingleAsync();
        stored.UserId.Should().Be(_operator);
        stored.Enabled.Should().BeTrue();
        stored.ArmState.Should().Be(TriggerArmState.Unseeded);
        stored.ArmCycle.Should().Be(0);
        stored.ConditionKind.Should().Be(TriggerConditionKind.IndicatorThreshold);
        stored.Symbol.Should().Be("ES");
        stored.Indicator.Should().Be("rsi");
    }

    [Theory]
    [InlineData("atr")]
    [InlineData("RSI")] // case-insensitive in, canonical (lower) out
    public async Task Create_ShouldAcceptAndCanonicaliseKnownIndicators(string indicator)
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Indicator = indicator }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        (await Context().Triggers.SingleAsync()).Indicator.Should().Be(indicator.ToLowerInvariant());
    }

    [Fact]
    public async Task Create_ShouldReject_WhenComparisonIsUnknown()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Comparison = IndicatorComparison.Unknown }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_ShouldReject_WhenPeriodIsNotPositive(int period)
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Period = period }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_ShouldReject_WhenResolutionIsNotPositive(int resolution)
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { ResolutionMinutes = resolution }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenTheIndicatorIsUnknown()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Indicator = "macd" }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenHysteresisIsNotPositive()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Hysteresis = 0m }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenTheRouteIsUnknown()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Route = TriggerRoute.Unknown }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReturn422_ForTheAgentReviewRoute_WhichIsNotYetAvailable()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Route = TriggerRoute.AgentReview }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    // --- Patch ---

    [Fact]
    public async Task Patch_ShouldEditReSeedAndBumpTheArmCycle_SoTheNextFireIsAFreshIncident()
    {
        // The trigger has already fired (cycle 2, currently Fired) -- its open incident key is trigger:{id}:2. An
        // edit must re-seed silently AND bump the cycle (the gh#385-review HIGH finding): without a fresh cycle the
        // next genuine crossing would mint the SAME key and be suppressed as a duplicate -- a silent miss.
        Guid id = await SeedTriggerAsync(_operator, armState: TriggerArmState.Fired, armCycle: 2);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.PatchTriggerAsync(
            id, new PatchTriggerRequest(Enabled: false, Threshold: 25m), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        TriggerRecord stored = await reload.Triggers.SingleAsync(t => t.Id == id);
        stored.Enabled.Should().BeFalse();
        stored.Threshold.Should().Be(25m);
        stored.ArmState.Should().Be(TriggerArmState.Unseeded); // re-seeded
        stored.ArmCycle.Should().Be(3);                        // bumped so the next fire's dedup key is fresh
    }

    [Fact]
    public async Task Patch_ShouldReEnableAndReSeed_WhenTheTriggerWasDisabled()
    {
        Guid id = await SeedTriggerAsync(_operator, armState: TriggerArmState.Fired, armCycle: 1);
        await using (TradingCopilotDbContext disable = Context())
        {
            await TriggerEndpoints.PatchTriggerAsync(
                id, new PatchTriggerRequest(Enabled: false), new FixedUser(_operator), disable, CancellationToken.None);
        }

        await using TradingCopilotDbContext enable = Context();
        IResult result = await TriggerEndpoints.PatchTriggerAsync(
            id, new PatchTriggerRequest(Enabled: true), new FixedUser(_operator), enable, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        TriggerRecord stored = await Context().Triggers.SingleAsync(t => t.Id == id);
        stored.Enabled.Should().BeTrue();
        stored.ArmState.Should().Be(TriggerArmState.Unseeded); // a condition true while disabled re-seeds, never fires on re-enable
    }

    [Fact]
    public async Task Patch_ShouldReturnNotFound_ForAnotherOwnersTrigger()
    {
        Guid theirs = await SeedTriggerAsync(Guid.NewGuid());
        await using TradingCopilotDbContext context = Context(); // as _operator

        IResult result = await TriggerEndpoints.PatchTriggerAsync(
            theirs, new PatchTriggerRequest(Enabled: false), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // --- Delete ---

    [Fact]
    public async Task Delete_ShouldRemoveTheCallersTrigger()
    {
        Guid id = await SeedTriggerAsync(_operator);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.DeleteTriggerAsync(id, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ShouldReturnNotFound_AndLeaveIntact_ForAnotherOwnersTrigger()
    {
        Guid otherOwner = Guid.NewGuid();
        Guid theirs = await SeedTriggerAsync(otherOwner);
        await using TradingCopilotDbContext context = Context(); // as _operator

        IResult result = await TriggerEndpoints.DeleteTriggerAsync(theirs, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await Context(otherOwner).Triggers.AnyAsync()).Should().BeTrue(); // survives, visible to its owner
    }

    // --- List (R-20) ---

    [Fact]
    public async Task List_ShouldReturnOnlyTheCallersTriggers()
    {
        await SeedTriggerAsync(_operator);
        await SeedTriggerAsync(_operator);
        await SeedTriggerAsync(Guid.NewGuid()); // another operator's

        await using TradingCopilotDbContext context = Context(); // as _operator
        IResult result = await TriggerEndpoints.ListTriggersAsync(context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        IReadOnlyList<TriggerResponse> list =
            ((IValueHttpResult)result).Value.Should().BeAssignableTo<IReadOnlyList<TriggerResponse>>().Subject;
        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_ShouldReturnTheCallersTrigger_And404ForAnothers()
    {
        Guid mine = await SeedTriggerAsync(_operator);
        Guid theirs = await SeedTriggerAsync(Guid.NewGuid());

        await using TradingCopilotDbContext context = Context();
        StatusOf(await TriggerEndpoints.GetTriggerAsync(mine, context, CancellationToken.None))
            .Should().Be(StatusCodes.Status200OK);
        StatusOf(await TriggerEndpoints.GetTriggerAsync(theirs, context, CancellationToken.None))
            .Should().Be(StatusCodes.Status404NotFound);
    }
}
