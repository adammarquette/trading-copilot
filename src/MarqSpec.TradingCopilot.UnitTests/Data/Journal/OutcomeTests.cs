using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Journal;

public class OutcomeTests
{
    private static Outcome AnOutcome() => new() { Resolution = OutcomeResolution.Loss };

    [Fact]
    public void NewOutcome_ShouldBeActive_WhenFirstConstructed()
    {
        Outcome outcome = AnOutcome();

        outcome.TrainingExcluded.Should().BeFalse();
        outcome.HiddenFromUser.Should().BeFalse();
        outcome.Deleted.Should().BeFalse();
    }

    [Fact]
    public void SoftDelete_ShouldSetAllThreeFlags_AsR15sReversibleCombinedShortcut()
    {
        // R-15: soft-delete is the combined shortcut -- excluded from training AND hidden, together, reversibly.
        Outcome outcome = AnOutcome();

        outcome.SoftDelete();

        outcome.Deleted.Should().BeTrue();
        outcome.TrainingExcluded.Should().BeTrue();
        outcome.HiddenFromUser.Should().BeTrue();
    }

    [Fact]
    public void Restore_ShouldClearAllThreeFlags_WhenReversingASoftDelete()
    {
        Outcome outcome = AnOutcome();
        outcome.SoftDelete();

        outcome.Restore();

        outcome.Deleted.Should().BeFalse();
        outcome.TrainingExcluded.Should().BeFalse();
        outcome.HiddenFromUser.Should().BeFalse();
    }

    [Fact]
    public void SetTrainingExcluded_ShouldExcludeFromTraining_WithoutHidingOrDeleting()
    {
        // The case R-15 exists for: a legitimate loss worth REVIEWING but not LEARNING from -- excluded from
        // training while staying visible, and not a soft-delete.
        Outcome outcome = AnOutcome();

        outcome.SetTrainingExcluded(true);

        outcome.TrainingExcluded.Should().BeTrue();
        outcome.HiddenFromUser.Should().BeFalse();
        outcome.Deleted.Should().BeFalse();
    }

    [Fact]
    public void SetHiddenFromUser_ShouldHide_WithoutTouchingTrainingOrDeleting()
    {
        // The mirror case: hidden from default views while still feeding training, independently.
        Outcome outcome = AnOutcome();

        outcome.SetHiddenFromUser(true);

        outcome.HiddenFromUser.Should().BeTrue();
        outcome.TrainingExcluded.Should().BeFalse();
        outcome.Deleted.Should().BeFalse();
    }
}
