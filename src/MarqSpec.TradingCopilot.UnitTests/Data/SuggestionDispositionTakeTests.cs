using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The pure take-disposition classifier (gh#549, R-8/R-9): comparing what the operator submitted against the
/// suggestion decides <see cref="SuggestionDispositionKind.Taken"/> vs <see cref="SuggestionDispositionKind.Modified"/>
/// and records which fields deviated -- the taken-vs-suggested delta the R-9 learning loop reads. The comparison is
/// <b>exact on the persisted decimals</b>: no tolerance may silently record a modified take as taken.
/// </summary>
public class SuggestionDispositionTakeTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Suggestion Suggested(
        decimal entry = 5230.25m, decimal stop = 5222.0m, decimal target = 5248.5m, int size = 2) => new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Instrument = "CON.F.US.EP.U26",
            Side = OrderSide.Buy,
            Size = size,
            EntryPrice = entry,
            StopPrice = stop,
            TargetPrice = target,
            Mode = TradingMode.Practice,
            State = SuggestionState.Active,
            CreatedAt = _now,
            Rationale = "seeded",
            CitedIndicator = "rsi",
            CitedPeriod = 14,
            CitedResolutionMinutes = 1,
            Confidence = 60,
            ExpiresAt = _now.AddHours(1),
        };

    [Fact]
    public void ForTake_ShouldBeTakenWithNoDeviation_WhenTheSubmittedParametersMatchTheSuggestion()
    {
        Suggestion suggestion = Suggested();

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, suggestion.EntryPrice, suggestion.StopPrice, suggestion.TargetPrice, suggestion.Size, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Taken, "every parameter matches the suggestion");
        disposition.Deviations.Should().Be(SuggestionDeviation.None);
        disposition.Reasons.Should().Be(SuggestionPassReason.None, "reasons are a pass concern, never a take");
        disposition.SuggestionId.Should().Be(suggestion.Id);
        disposition.UserId.Should().Be(suggestion.UserId, "the disposition inherits the suggestion's owner (R-20)");
        // The take-time snapshot is captured even for an unmodified take (the journal is self-contained).
        disposition.TakenEntryPrice.Should().Be(suggestion.EntryPrice);
        disposition.TakenStopPrice.Should().Be(suggestion.StopPrice);
        disposition.TakenTargetPrice.Should().Be(suggestion.TargetPrice);
        disposition.TakenSize.Should().Be(suggestion.Size);
        disposition.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public void ForTake_ShouldBeModifiedWithAStopDeviation_WhenOnlyTheWorkingStopChanged()
    {
        // The wireframe's case: the operator widened the stop (5222.0 -> 5218.0) and sent it.
        Suggestion suggestion = Suggested();

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, suggestion.EntryPrice, submittedStop: 5218.0m, suggestion.TargetPrice, suggestion.Size, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Modified);
        disposition.Deviations.Should().Be(SuggestionDeviation.Stop, "only the stop moved");
        disposition.TakenStopPrice.Should().Be(5218.0m, "the submitted stop is the snapshot, not the suggested one");
    }

    [Fact]
    public void ForTake_ShouldBeModified_ForAOneTickChange_NeverTaken()
    {
        // Exact comparison, no tolerance (gh#549): a single-tick change is a real modification.
        Suggestion suggestion = Suggested(stop: 5222.0m);

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, suggestion.EntryPrice, submittedStop: 5221.75m, suggestion.TargetPrice, suggestion.Size, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Modified, "a one-tick move is not a match");
        disposition.Deviations.Should().HaveFlag(SuggestionDeviation.Stop);
    }

    [Fact]
    public void ForTake_ShouldNotFlagADeviation_ForTheSameValueAtADifferentScale()
    {
        // 5222.0 and 5222.00 are the SAME price -- decimal value equality, not string equality. No deviation.
        Suggestion suggestion = Suggested(stop: 5222.0m);

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, suggestion.EntryPrice, submittedStop: 5222.00m, suggestion.TargetPrice, suggestion.Size, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Taken);
        disposition.Deviations.Should().Be(SuggestionDeviation.None);
    }

    [Fact]
    public void ForTake_ShouldFlagATargetDeviation_WhenTheTakeCarriesNoTarget()
    {
        // The order's take-profit is optional; removing it from a suggestion that had one is a Target deviation.
        Suggestion suggestion = Suggested(target: 5248.5m);

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, suggestion.EntryPrice, suggestion.StopPrice, submittedTarget: null, suggestion.Size, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Modified);
        disposition.Deviations.Should().Be(SuggestionDeviation.Target);
        disposition.TakenTargetPrice.Should().BeNull();
    }

    [Fact]
    public void ForTake_ShouldRecordEveryDeviatedField_WhenSeveralChanged()
    {
        Suggestion suggestion = Suggested(entry: 5230.25m, stop: 5222.0m, target: 5248.5m, size: 2);

        SuggestionDisposition disposition = SuggestionDisposition.ForTake(
            suggestion, submittedEntry: 5231.0m, submittedStop: 5222.0m, submittedTarget: 5248.5m, submittedSize: 1, _now);

        disposition.Kind.Should().Be(SuggestionDispositionKind.Modified);
        disposition.Deviations.Should().Be(SuggestionDeviation.Entry | SuggestionDeviation.Size, "entry and size moved, stop and target held");
    }
}
