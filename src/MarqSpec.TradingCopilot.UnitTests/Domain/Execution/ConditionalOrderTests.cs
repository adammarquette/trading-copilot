using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

/// <summary>
/// The synthetic conditional entry order (gh#176, ADR-0007): the deterministic fire/cancel decisions a watcher
/// will consult, and the one-way transitions that stop a retrying watcher re-firing a resolved order. The
/// safety-relevant behaviours: an unset cross direction never fires, a cancel band on the wrong side is refused,
/// and nothing fires or cancels once the order has left <c>Pending</c>.
/// </summary>
public class ConditionalOrderTests
{
    private static InstrumentSpec Es => InstrumentSpec.Create(InstrumentId.Parse("ES"), 0.25m, 50m);

    private static OrderProposal Proposal(OrderSide side = OrderSide.Buy) => new(
        Es, side, 2, new Price(5_000m), new Price(4_995m), new Price(4_990m), new Price(5_000m));

    private static ConditionalTrigger Trigger(ConditionalCrossDirection direction, decimal price = 5_010m) =>
        new(new Price(price), direction);

    // --- ShouldFire: the trigger crosses (gh#176) ---

    [Theory]
    [InlineData(ConditionalCrossDirection.RisesTo, 5_009, false)] // below a rise-to trigger -> waits
    [InlineData(ConditionalCrossDirection.RisesTo, 5_010, true)]  // reaches it -> fires
    [InlineData(ConditionalCrossDirection.RisesTo, 5_011, true)]  // through it -> fires
    [InlineData(ConditionalCrossDirection.FallsTo, 5_011, false)] // above a fall-to trigger -> waits
    [InlineData(ConditionalCrossDirection.FallsTo, 5_010, true)]  // reaches it -> fires
    [InlineData(ConditionalCrossDirection.FallsTo, 5_009, true)]  // through it -> fires
    public void ShouldFire_ShouldTrackTheCrossDirection(ConditionalCrossDirection direction, int price, bool expected)
    {
        ConditionalOrder order = ConditionalOrder.Create(Proposal(), Trigger(direction));

        order.ShouldFire(new Price(price)).Should().Be(expected);
    }

    [Fact]
    public void ShouldFire_ShouldNeverFire_ForAnUnknownDirection_EvenAtTheTriggerPrice()
    {
        // Fail-closed: a trigger with no declared direction rests rather than firing on a guessed side. (Create
        // refuses one, so this exercises the trigger primitive directly -- the last line of defence.)
        ConditionalTrigger unset = new(new Price(5_010m), ConditionalCrossDirection.Unknown);

        unset.HasFired(new Price(5_010m)).Should().BeFalse();
        unset.HasFired(new Price(9_999m)).Should().BeFalse();
    }

    // --- ShouldCancel: stale-setup and validity window (gh#176) ---

    [Fact]
    public void ShouldCancel_ShouldCancel_WhenTheValidityWindowHasPassed()
    {
        DateTimeOffset armed = DateTimeOffset.UnixEpoch;
        ConditionalOrder order = ConditionalOrder.Create(
            Proposal(), Trigger(ConditionalCrossDirection.RisesTo), expiresAt: armed.AddMinutes(30));

        order.ShouldCancel(new Price(5_000m), armed.AddMinutes(29)).Should().BeFalse(); // still inside the window
        order.ShouldCancel(new Price(5_000m), armed.AddMinutes(30)).Should().BeTrue();  // deadline reached
    }

    [Theory]
    // A RisesTo order goes stale when price falls away BELOW the band (4980); a FallsTo order when it rises ABOVE it.
    [InlineData(ConditionalCrossDirection.RisesTo, 4_980, 4_981, false)]
    [InlineData(ConditionalCrossDirection.RisesTo, 4_980, 4_980, true)]
    [InlineData(ConditionalCrossDirection.FallsTo, 5_040, 5_039, false)]
    [InlineData(ConditionalCrossDirection.FallsTo, 5_040, 5_040, true)]
    public void ShouldCancel_ShouldCancel_WhenPriceDriftsPastTheBand(
        ConditionalCrossDirection direction, int band, int price, bool expected)
    {
        // Both trigger at 5010: a RisesTo's stale band 4980 sits below it, a FallsTo's band 5040 above it.
        ConditionalOrder order = ConditionalOrder.Create(
            Proposal(), Trigger(direction, 5_010m), cancelDrift: new Price(band));

        order.ShouldCancel(new Price(price), DateTimeOffset.UnixEpoch).Should().Be(expected);
    }

    [Fact]
    public void ShouldCancel_ShouldNotCancel_WithNeitherDriftNorExpiry()
    {
        ConditionalOrder order = ConditionalOrder.Create(Proposal(), Trigger(ConditionalCrossDirection.RisesTo));

        order.ShouldCancel(new Price(1m), DateTimeOffset.MaxValue).Should().BeFalse();
    }

    // --- Create: the invariants (gh#176) ---

    [Fact]
    public void Create_ShouldRefuse_AnUnknownCrossDirection()
    {
        Action act = () => ConditionalOrder.Create(Proposal(), Trigger(ConditionalCrossDirection.Unknown));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(ConditionalCrossDirection.RisesTo, 5_020)] // a RisesTo band ABOVE the 5010 trigger -- wrong side
    [InlineData(ConditionalCrossDirection.FallsTo, 5_000)] // a FallsTo band BELOW the 5010 trigger -- wrong side
    public void Create_ShouldRefuse_ACancelBandOnTheWrongSideOfTheTrigger(
        ConditionalCrossDirection direction, int band)
    {
        Action act = () => ConditionalOrder.Create(
            Proposal(), Trigger(direction, 5_010m), cancelDrift: new Price(band));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_ShouldStartPending()
    {
        ConditionalOrder order = ConditionalOrder.Create(Proposal(), Trigger(ConditionalCrossDirection.RisesTo));

        order.Status.Should().Be(ConditionalStatus.Pending);
    }

    // --- Transitions: one-way and idempotent (gh#176) ---

    [Fact]
    public void Fire_ShouldMoveToFired_AndStopFiring()
    {
        ConditionalOrder pending = ConditionalOrder.Create(Proposal(), Trigger(ConditionalCrossDirection.RisesTo));

        ConditionalOrder fired = pending.Fire();

        fired.Status.Should().Be(ConditionalStatus.Fired);
        fired.ShouldFire(new Price(9_999m)).Should().BeFalse();          // resolved -- never fires again
        fired.ShouldCancel(new Price(1m), DateTimeOffset.MaxValue).Should().BeFalse();
    }

    [Fact]
    public void Transitions_ShouldBeOneWay_SoAResolvedOrderNeverChangesState()
    {
        ConditionalOrder cancelled = ConditionalOrder
            .Create(Proposal(), Trigger(ConditionalCrossDirection.RisesTo))
            .Cancel();

        cancelled.Status.Should().Be(ConditionalStatus.Cancelled);
        cancelled.Fire().Status.Should().Be(ConditionalStatus.Cancelled);   // cannot fire a cancelled order
        cancelled.Expire().Status.Should().Be(ConditionalStatus.Cancelled); // nor expire it
    }

    [Fact]
    public void Fire_ShouldBeIdempotent_ForARetryingWatcher()
    {
        ConditionalOrder fired = ConditionalOrder
            .Create(Proposal(), Trigger(ConditionalCrossDirection.RisesTo))
            .Fire();

        fired.Fire().Status.Should().Be(ConditionalStatus.Fired); // re-firing is a harmless no-op
    }
}
