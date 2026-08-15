using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The <c>/api/triggers</c> handlers (gh#385, R-4 / R-7) — the operator authors and manages standing triggers. The
/// safety-relevant behaviours: a create is validated whole at the boundary (bad enum, non-positive period /
/// resolution, unknown indicator, the not-yet-available agent-review route are all refused, nothing partially
/// stored); a create starts Enabled + <b>Unconfirmed</b> (gh#470) + Unseeded + cycle 0, so authorship arms nothing
/// until a separate confirm; an edit re-seeds the arm state without touching the incident cycle; and every
/// read/write is R-20-scoped to the caller.
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
        int armCycle = 0,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed,
        Guid? sourceRuleId = null,
        Guid? sourceConversationId = null)
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
            Confirmation = confirmation,
            ArmState = armState,
            ArmCycle = armCycle,
            SourceRuleId = sourceRuleId,
            SourceConversationId = sourceConversationId,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedAccountAsync(Guid owner, TradingMode mode = TradingMode.Practice)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(owner);
        context.Accounts.Add(new Account
        {
            Id = id,
            UserId = owner,
            ConnectionId = Guid.NewGuid(),
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Mode = mode,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static CreateTriggerRequest AgentReviewRequest(Guid accountId, int size = 2) =>
        ValidRequest() with { Route = TriggerRoute.AgentReview, AccountId = accountId, Size = size };

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
        stored.Confirmation.Should().Be(
            TriggerConfirmation.Unconfirmed,
            "a newly authored trigger is inert until the operator confirms it, even though Enabled is true (gh#470)");
        stored.ArmState.Should().Be(TriggerArmState.Unseeded);
        stored.ArmCycle.Should().Be(0);
        stored.ConditionKind.Should().Be(TriggerConditionKind.IndicatorThreshold);
        stored.Symbol.Should().Be("ES");
        stored.Indicator.Should().Be("rsi");
    }

    [Fact]
    public async Task GetTrigger_ShouldExposeTheSourceOrigin_WhenAuthoredFromARuleAndConversation()
    {
        // A trigger a rule minted from a conversation must answer "why does this exist?" at the READ path — the origin
        // is recoverable without a database query (R-7, gh#471). Both references are SOFT (no FK), so the trigger
        // outlives either the rule or the conversation.
        Guid ruleId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        Guid triggerId = await SeedTriggerAsync(
            _operator, sourceRuleId: ruleId, sourceConversationId: conversationId);

        IResult result = await TriggerEndpoints.GetTriggerAsync(triggerId, Context(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        TriggerResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<TriggerResponse>().Subject;
        response.SourceRuleId.Should().Be(ruleId);
        response.SourceConversationId.Should().Be(conversationId);
    }

    [Fact]
    public async Task Create_ShouldLeaveTheSourceOriginNull_WhenAuthoredOverTheApi()
    {
        // A trigger authored directly over the API has no rule or conversation behind it: the provenance is null, not
        // a requirement (gh#471). The reference is for triggers a rule minted from chat, never operator-hand-authored ones.
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        TriggerResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<TriggerResponse>().Subject;
        response.SourceRuleId.Should().BeNull();
        response.SourceConversationId.Should().BeNull();
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

    // --- Create: the mechanical route takes no account or size ---

    [Fact]
    public async Task Create_ShouldStoreNoAccountOrSize_ForAMechanicalTrigger()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        TriggerRecord stored = await Context().Triggers.SingleAsync();
        stored.AccountId.Should().BeNull();
        stored.Size.Should().BeNull();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenMechanicalCarriesAnAccount()
    {
        Guid accountId = await SeedAccountAsync(_operator);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { AccountId = accountId }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenMechanicalCarriesASize()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Size = 1 }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    // --- Create: the agent-review route requires an owned, mode-declared account and a positive size ---

    [Fact]
    public async Task Create_ShouldPersistAnAgentReviewTrigger_ThatRoundTripsItsAccountAndSize()
    {
        Guid accountId = await SeedAccountAsync(_operator, TradingMode.Practice);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            AgentReviewRequest(accountId, size: 3), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        TriggerResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<TriggerResponse>().Subject;
        response.Route.Should().Be(TriggerRoute.AgentReview);
        response.AccountId.Should().Be(accountId);
        response.Size.Should().Be(3);

        TriggerRecord stored = await Context().Triggers.SingleAsync();
        stored.AccountId.Should().Be(accountId);
        stored.Size.Should().Be(3);
    }

    [Fact]
    public async Task Create_ShouldReject_WhenAgentReviewHasNoAccount()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Route = TriggerRoute.AgentReview, Size = 2 },
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_ShouldReject_WhenAgentReviewSizeIsNotPositive(int size)
    {
        Guid accountId = await SeedAccountAsync(_operator);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Route = TriggerRoute.AgentReview, AccountId = accountId, Size = size },
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenAgentReviewHasNoSize()
    {
        Guid accountId = await SeedAccountAsync(_operator);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            ValidRequest() with { Route = TriggerRoute.AgentReview, AccountId = accountId }, // Size left null
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReturnNotFound_WhenAgentReviewAccountIsAnothersOrMissing()
    {
        Guid theirs = await SeedAccountAsync(Guid.NewGuid()); // another operator's -- the R-20 filter hides it
        await using TradingCopilotDbContext context = Context(); // as _operator

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            AgentReviewRequest(theirs), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await Context().Triggers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReject_WhenAgentReviewAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAccountAsync(_operator, TradingMode.Undeclared);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.CreateTriggerAsync(
            AgentReviewRequest(accountId), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
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

    // --- Confirm (gh#470) ---

    [Fact]
    public async Task Confirm_ShouldMarkTheTriggerConfirmed_SoTheScanCanEvaluateIt()
    {
        // A freshly authored trigger is Unconfirmed (inert). Confirming is the deliberate, separate act that accepts
        // it into the firing set -- the operator's confirmation step between authorship and armed.
        Guid id = await SeedTriggerAsync(_operator, confirmation: TriggerConfirmation.Unconfirmed);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.ConfirmTriggerAsync(id, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        TriggerRecord stored = await Context().Triggers.SingleAsync(t => t.Id == id);
        stored.Confirmation.Should().Be(TriggerConfirmation.Confirmed);
    }

    [Fact]
    public async Task Confirm_ShouldBeIdempotent_WhenTheTriggerIsAlreadyConfirmed()
    {
        // A retried confirm request must be harmless: still 200, still Confirmed, nothing about the debounce disturbed.
        Guid id = await SeedTriggerAsync(_operator, armState: TriggerArmState.Fired, armCycle: 3);
        await using TradingCopilotDbContext context = Context();

        IResult result = await TriggerEndpoints.ConfirmTriggerAsync(id, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        TriggerRecord stored = await Context().Triggers.SingleAsync(t => t.Id == id);
        stored.Confirmation.Should().Be(TriggerConfirmation.Confirmed);
        stored.ArmState.Should().Be(TriggerArmState.Fired); // confirm leaves the debounce untouched
        stored.ArmCycle.Should().Be(3);
    }

    [Fact]
    public async Task Confirm_ShouldReturnNotFound_ForAnotherOwnersTrigger()
    {
        // The R-20 filter hides another operator's trigger, so confirming one reads as 404 -- never a cross-owner write.
        Guid theirs = await SeedTriggerAsync(Guid.NewGuid(), confirmation: TriggerConfirmation.Unconfirmed);
        await using TradingCopilotDbContext context = Context(); // as _operator

        IResult result = await TriggerEndpoints.ConfirmTriggerAsync(theirs, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await Context(Guid.Empty).Triggers.IgnoreQueryFilters().SingleAsync(t => t.Id == theirs))
            .Confirmation.Should().Be(TriggerConfirmation.Unconfirmed, "the other owner's trigger is untouched");
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
