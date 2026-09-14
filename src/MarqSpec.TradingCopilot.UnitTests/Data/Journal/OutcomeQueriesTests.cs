using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Journal;
using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Journal;

public class OutcomeQueriesTests
{
    private static Outcome Active() => new() { Id = Guid.NewGuid(), Resolution = OutcomeResolution.Win };

    private static Outcome SoftDeleted()
    {
        Outcome outcome = new() { Id = Guid.NewGuid(), Resolution = OutcomeResolution.Loss };
        outcome.SoftDelete();
        return outcome;
    }

    private static Outcome TrainingExcludedButVisible()
    {
        Outcome outcome = new() { Id = Guid.NewGuid(), Resolution = OutcomeResolution.Loss };
        outcome.SetTrainingExcluded(true);
        return outcome;
    }

    private static Outcome HiddenButNotDeleted()
    {
        Outcome outcome = new() { Id = Guid.NewGuid(), Resolution = OutcomeResolution.Win };
        outcome.SetHiddenFromUser(true);
        return outcome;
    }

    private static Outcome SoftDeletedThenTrainingReEnabled()
    {
        // The flags are independent: a soft-deleted row whose training exclusion is later toggled OFF stays deleted.
        Outcome outcome = new() { Id = Guid.NewGuid(), Resolution = OutcomeResolution.Loss };
        outcome.SoftDelete();
        outcome.SetTrainingExcluded(false);
        return outcome;
    }

    [Fact]
    public void ExcludingDeleted_ShouldDropSoftDeletedRows_KeepingTheRest()
    {
        Outcome active = Active();
        IQueryable<Outcome> outcomes = new[] { active, SoftDeleted() }.AsQueryable();

        Outcome[] kept = outcomes.ExcludingDeleted().ToArray();

        kept.Should().ContainSingle().Which.Should().BeSameAs(active);
    }

    [Fact]
    public void IncludingDeleted_ShouldKeepSoftDeletedRows_WhenAskedToInclude()
    {
        IQueryable<Outcome> outcomes = new[] { Active(), SoftDeleted() }.AsQueryable();

        outcomes.IncludingDeleted(include: true).Should().HaveCount(2);
    }

    [Fact]
    public void IncludingDeleted_ShouldMatchExcludingDeleted_WhenAskedNotToInclude()
    {
        // The R-15 report toggle: the same query with and without soft-deleted records -- and the two figures differ.
        IQueryable<Outcome> outcomes = new[] { Active(), SoftDeleted() }.AsQueryable();

        int inclusive = outcomes.IncludingDeleted(include: true).Count();
        int exclusive = outcomes.IncludingDeleted(include: false).Count();

        exclusive.Should().Be(outcomes.ExcludingDeleted().Count());
        exclusive.Should().Be(1);
        inclusive.Should().Be(2);
    }

    [Fact]
    public void Visible_ShouldDropDeletedAndHiddenRows_KeepingTheVisibleOnes()
    {
        // The default journal view honours BOTH deleted and hidden_from_user -- a hidden-but-training row is not
        // shown, while a training-excluded-but-visible row still is.
        Outcome active = Active();
        Outcome excludedButVisible = TrainingExcludedButVisible();
        IQueryable<Outcome> outcomes = new[] { active, SoftDeleted(), HiddenButNotDeleted(), excludedButVisible }.AsQueryable();

        Outcome[] visible = outcomes.Visible().ToArray();

        visible.Should().BeEquivalentTo(new[] { active, excludedButVisible });
    }

    [Fact]
    public void ForTrainingSignal_ShouldDropTrainingExcludedRows_EvenWhenStillVisible()
    {
        // No suggestion-engine training path reads a training_excluded row -- including one kept visible.
        Outcome active = Active();
        IQueryable<Outcome> outcomes = new[] { active, TrainingExcludedButVisible() }.AsQueryable();

        outcomes.ForTrainingSignal().ToArray().Should().ContainSingle().Which.Should().BeSameAs(active);
    }

    [Fact]
    public void ForTrainingSignal_ShouldDropSoftDeletedRows_EvenWhenTrainingWasReEnabled()
    {
        // Soft-delete excludes from ALL training; the flags being independent must not let a re-enabled training
        // toggle leak a deleted row back into the learning set.
        Outcome active = Active();
        IQueryable<Outcome> outcomes = new[] { active, SoftDeletedThenTrainingReEnabled() }.AsQueryable();

        outcomes.ForTrainingSignal().ToArray().Should().ContainSingle().Which.Should().BeSameAs(active);
    }
}
