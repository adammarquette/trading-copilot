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

    [Fact]
    public void ExcludingDeleted_ShouldDropSoftDeletedRows_KeepingTheRest()
    {
        Outcome active = Active();
        IQueryable<Outcome> outcomes = new[] { active, SoftDeleted() }.AsQueryable();

        Outcome[] visible = outcomes.ExcludingDeleted().ToArray();

        visible.Should().ContainSingle().Which.Should().BeSameAs(active);
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
        // The R-15 report toggle: the same query with and without excluded records -- and the two figures differ.
        IQueryable<Outcome> outcomes = new[] { Active(), SoftDeleted() }.AsQueryable();

        int inclusive = outcomes.IncludingDeleted(include: true).Count();
        int exclusive = outcomes.IncludingDeleted(include: false).Count();

        exclusive.Should().Be(outcomes.ExcludingDeleted().Count());
        exclusive.Should().Be(1);
        inclusive.Should().Be(2);
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
    public void ForTrainingSignal_ShouldDropSoftDeletedRows_SinceSoftDeleteExcludesFromTraining()
    {
        IQueryable<Outcome> outcomes = new[] { Active(), SoftDeleted() }.AsQueryable();

        outcomes.ForTrainingSignal().Should().ContainSingle();
    }
}
