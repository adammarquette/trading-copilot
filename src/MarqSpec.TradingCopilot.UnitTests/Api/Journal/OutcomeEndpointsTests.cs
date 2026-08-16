using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Journal;

/// <summary>
/// The <c>/outcomes</c> read + R-15 removal surface (gh#909, R-9 / R-15). The behaviours that matter: the report
/// toggle includes or excludes soft-deleted rows; a foreign outcome is a <b>404</b> (R-20); soft-delete moves all
/// three removal flags together while the independent toggles move one at a time — and go through <see cref="Outcome"/>'s
/// own methods, so the R-15 invariant cannot be bypassed.
/// </summary>
public class OutcomeEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private async Task<Guid> SeedOutcomeAsync(Guid? owner = null, Action<Outcome>? configure = null)
    {
        Guid id = Guid.NewGuid();
        Guid u = owner ?? _operator;
        Outcome outcome = new() { Id = id, UserId = u, TradeId = Guid.NewGuid(), Resolution = OutcomeResolution.Win };
        configure?.Invoke(outcome);

        await using TradingCopilotDbContext context = Context(u);
        context.Outcomes.Add(outcome);
        await context.SaveChangesAsync();
        return id;
    }

    // -- list + report toggle -------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ShouldExcludeSoftDeletedRows_ByDefault()
    {
        Guid kept = await SeedOutcomeAsync();
        await SeedOutcomeAsync(configure: outcome => outcome.SoftDelete());

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.ListAsync(includeDeleted: null, context, default);

        OutcomeListResponse list = ValueOf<OutcomeListResponse>(result);
        list.Outcomes.Should().ContainSingle().Which.Id.Should().Be(kept);
    }

    [Fact]
    public async Task ListAsync_ShouldIncludeSoftDeletedRows_WhenAskedToInclude()
    {
        await SeedOutcomeAsync();
        await SeedOutcomeAsync(configure: outcome => outcome.SoftDelete());

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.ListAsync(includeDeleted: true, context, default);

        ValueOf<OutcomeListResponse>(result).Outcomes.Should().HaveCount(2); // the R-15 report toggle: both figures
    }

    [Fact]
    public async Task ListAsync_ShouldNotReturnAnotherOperatorsOutcomes()
    {
        await SeedOutcomeAsync(owner: Guid.NewGuid()); // a stranger's outcome

        await using TradingCopilotDbContext context = Context(); // the caller
        IResult result = await OutcomeEndpoints.ListAsync(includeDeleted: true, context, default);

        ValueOf<OutcomeListResponse>(result).Outcomes.Should().BeEmpty(); // R-20 default-deny
    }

    // -- soft delete / restore ------------------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeleteAsync_ShouldMoveAllThreeRemovalFlagsTogether_AndReturnTheUpdatedOutcome()
    {
        Guid id = await SeedOutcomeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.SoftDeleteAsync(id, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        OutcomeResponse response = ValueOf<OutcomeResponse>(result);
        response.Deleted.Should().BeTrue();
        response.TrainingExcluded.Should().BeTrue();
        response.HiddenFromUser.Should().BeTrue();
    }

    [Fact]
    public async Task SoftDeleteAsync_ShouldReturnNotFound_ForAForeignOutcome_AndLeaveItUntouched()
    {
        Guid strangerOwner = Guid.NewGuid();
        Guid id = await SeedOutcomeAsync(owner: strangerOwner);

        await using TradingCopilotDbContext caller = Context(); // a different operator
        IResult result = await OutcomeEndpoints.SoftDeleteAsync(id, caller, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound); // R-20 — never a disclosure

        await using TradingCopilotDbContext verify = Context(strangerOwner);
        Outcome untouched = await verify.Outcomes.SingleAsync(outcome => outcome.Id == id);
        untouched.Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreAsync_ShouldClearAllThreeRemovalFlags()
    {
        Guid id = await SeedOutcomeAsync(configure: outcome => outcome.SoftDelete());

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.RestoreAsync(id, context, default);

        OutcomeResponse response = ValueOf<OutcomeResponse>(result);
        response.Deleted.Should().BeFalse();
        response.TrainingExcluded.Should().BeFalse();
        response.HiddenFromUser.Should().BeFalse();
    }

    // -- independent toggles --------------------------------------------------------------------------------------

    [Fact]
    public async Task SetTrainingExclusionAsync_ShouldExcludeFromTraining_IndependentlyOfVisibility()
    {
        Guid id = await SeedOutcomeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.SetTrainingExclusionAsync(id, new OutcomeFlagRequest(true), context, default);

        OutcomeResponse response = ValueOf<OutcomeResponse>(result);
        response.TrainingExcluded.Should().BeTrue();
        response.HiddenFromUser.Should().BeFalse(); // independent — training exclusion does not hide
        response.Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task SetVisibilityAsync_ShouldHide_IndependentlyOfTraining()
    {
        Guid id = await SeedOutcomeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.SetVisibilityAsync(id, new OutcomeFlagRequest(true), context, default);

        OutcomeResponse response = ValueOf<OutcomeResponse>(result);
        response.HiddenFromUser.Should().BeTrue();
        response.TrainingExcluded.Should().BeFalse(); // independent — hiding does not exclude from training
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetTrainingExclusionAsync_ShouldReturnBadRequest_WhenTheBodyIsMissing(bool _)
    {
        Guid id = await SeedOutcomeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.SetTrainingExclusionAsync(id, request: null, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SetVisibilityAsync_ShouldReturnBadRequest_WhenTheBodyIsMissing()
    {
        Guid id = await SeedOutcomeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.SetVisibilityAsync(id, request: null, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SetVisibilityAsync_ShouldReturnNotFound_ForAForeignOutcome()
    {
        Guid id = await SeedOutcomeAsync(owner: Guid.NewGuid());

        await using TradingCopilotDbContext caller = Context();
        IResult result = await OutcomeEndpoints.SetVisibilityAsync(id, new OutcomeFlagRequest(true), caller, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }
}
