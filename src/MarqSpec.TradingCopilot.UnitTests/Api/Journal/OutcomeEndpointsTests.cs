using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Journal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    // An UNTAKEN outcome (no TradeId, a SuggestionId parent). Hard delete now removes it AND both kinds via a
    // recomposition-suppression tombstone (gh#955). CK_Outcomes_ParentPresent is not enforced in-memory, but a
    // SuggestionId keeps the seed shaped like a real untaken row.
    private Task<Guid> SeedUntakenOutcomeAsync(Guid? owner = null, Action<Outcome>? configure = null) =>
        SeedOutcomeAsync(owner, outcome =>
        {
            outcome.TradeId = null;
            outcome.SuggestionId = Guid.NewGuid();
            configure?.Invoke(outcome);
        });

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

    // -- hard delete + audit ------------------------------------------------------------------------------------

    private static readonly DateTimeOffset _now = new(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

    private static IAuditLog OkAudit()
    {
        IAuditLog audit = A.Fake<IAuditLog>();
        A.CallTo(() => audit.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        return audit;
    }

    [Theory]
    [InlineData(false)] // trade-derived (TradeId set) — the tombstone keys on the trade (the closed-trade sweep's key)
    [InlineData(true)]  // untaken (SuggestionId only) — the tombstone keys on the suggestion (the unfilled sweep, gh#939)
    public async Task HardDeleteAsync_ShouldRemoveTheRow_WriteASuppressionTombstone_AndAudit(bool untaken)
    {
        // Hard delete is the confirmed, irreversible removal (R-15) and now STICKS (gh#955): it removes the row AND
        // writes a recomposition-suppression tombstone in the same unit of work — so the OutcomeJournalService sweep
        // does not re-derive it — then records the deletion fact to the audit trail. The tombstone is keyed on the
        // outcome's OWN recomposition source: its TradeId when it had a trade, else its SuggestionId.
        Guid id = untaken ? await SeedUntakenOutcomeAsync() : await SeedOutcomeAsync();
        IAuditLog audit = OkAudit();

        Guid? expectedTradeId;
        Guid? expectedSuggestionId;
        await using (TradingCopilotDbContext read = Context())
        {
            Outcome seeded = await read.Outcomes.SingleAsync(outcome => outcome.Id == id);
            expectedTradeId = seeded.TradeId;
            expectedSuggestionId = seeded.TradeId is not null ? null : seeded.SuggestionId; // one key: the trade's, else the suggestion's
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.HardDeleteAsync(id, _now, context, audit, NullLoggerFactory.Instance, default);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        A.CallTo(() => audit.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // the deletion fact is recorded (R-15)

        await using TradingCopilotDbContext verify = Context();
        (await verify.Outcomes.AnyAsync(outcome => outcome.Id == id)).Should().BeFalse(); // the row is gone

        OutcomeSuppression tombstone = (await verify.OutcomeSuppressions.IgnoreQueryFilters().ToListAsync())
            .Should().ContainSingle().Subject;
        tombstone.TradeId.Should().Be(expectedTradeId);
        tombstone.SuggestionId.Should().Be(expectedSuggestionId);
        tombstone.UserId.Should().Be(_operator); // R-20: stamped from the deleted outcome's owner
        tombstone.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public async Task HardDeleteAsync_ShouldStillRemoveAndSuppress_WhenTheAuditWriteFails()
    {
        // The remove + suppression have COMMITTED before the audit runs (the secondary-write posture): a transient
        // audit fault must not resurrect a row the operator deliberately removed, nor undo the suppression. The failure
        // is logged, never surfaced.
        Guid id = await SeedOutcomeAsync();
        IAuditLog audit = A.Fake<IAuditLog>();
        A.CallTo(() => audit.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit sink down"));

        await using TradingCopilotDbContext context = Context();
        IResult result = await OutcomeEndpoints.HardDeleteAsync(id, _now, context, audit, NullLoggerFactory.Instance, default);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent); // audit failure swallowed
        await using TradingCopilotDbContext verify = Context();
        (await verify.Outcomes.AnyAsync(outcome => outcome.Id == id)).Should().BeFalse(); // still removed
        (await verify.OutcomeSuppressions.IgnoreQueryFilters().AnyAsync()).Should().BeTrue(); // still suppressed
    }

    [Fact]
    public async Task HardDeleteAsync_ShouldReturnNotFound_ForAForeignOutcome_AndNeitherDeleteNorAudit()
    {
        Guid strangerOwner = Guid.NewGuid();
        Guid id = await SeedOutcomeAsync(owner: strangerOwner);
        IAuditLog audit = OkAudit();

        await using TradingCopilotDbContext caller = Context();
        IResult result = await OutcomeEndpoints.HardDeleteAsync(id, _now, caller, audit, NullLoggerFactory.Instance, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound); // R-20
        A.CallTo(() => audit.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .MustNotHaveHappened(); // a 404 removes nothing, so it audits nothing

        await using TradingCopilotDbContext verify = Context(strangerOwner);
        (await verify.Outcomes.AnyAsync(outcome => outcome.Id == id)).Should().BeTrue(); // untouched
        (await verify.OutcomeSuppressions.IgnoreQueryFilters().AnyAsync()).Should().BeFalse(); // and nothing suppressed
    }

}
