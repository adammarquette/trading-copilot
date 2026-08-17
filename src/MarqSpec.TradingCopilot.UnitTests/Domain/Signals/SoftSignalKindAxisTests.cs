using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Signals;

/// <summary>
/// The kind→axis mapping (gh#762, ADR-0014): star / mute are the <b>importance</b> axis (magnitude → salience),
/// 👍/👎 the <b>direction</b> axis (sentiment → R-9 learning), and <see cref="SoftSignalKind.Unknown"/> (or any
/// undefined value) has no axis and is refused. This mapping is the single source of truth the store's two filtered
/// unique indexes, the write path's replace-within-axis, and salience's importance-only accumulation all rely on, so
/// it is pinned exhaustively here.
/// </summary>
public class SoftSignalKindAxisTests
{
    [Theory]
    [InlineData(SoftSignalKind.Star, SoftSignalAxis.Importance)]
    [InlineData(SoftSignalKind.Mute, SoftSignalAxis.Importance)]
    [InlineData(SoftSignalKind.ThumbsUp, SoftSignalAxis.Direction)]
    [InlineData(SoftSignalKind.ThumbsDown, SoftSignalAxis.Direction)]
    public void Axis_MapsEachRealKind_ToItsAxis(SoftSignalKind kind, SoftSignalAxis expected)
    {
        kind.Axis().Should().Be(expected);
    }

    [Fact]
    public void Axis_OfUnknown_IsRefused()
    {
        // Unknown is the refusable zero -- it has no axis (the store refuses it too), so mapping it throws rather
        // than inventing an axis a caller might act on.
        Action map = () => SoftSignalKind.Unknown.Axis();

        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Axis_OfAnUndefinedValue_IsRefused()
    {
        Action map = () => ((SoftSignalKind)99).Axis();

        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EveryDefinedKind_ExceptUnknown_HasExactlyOneAxis()
    {
        // Exhaustive guard: a new kind added to the enum fails this until its axis is declared, so a kind can never
        // silently land with no axis -- which the write path (replace-within-axis) and the store (per-axis unique
        // indexes) both depend on.
        foreach (SoftSignalKind kind in Enum.GetValues<SoftSignalKind>())
        {
            if (kind == SoftSignalKind.Unknown)
            {
                continue;
            }

            kind.Axis().Should().BeOneOf(SoftSignalAxis.Importance, SoftSignalAxis.Direction);
        }
    }
}
