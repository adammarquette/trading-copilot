using MarqSpec.TradingCopilot.Integration.Tradovate;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The register of live Tradovate quote subscriptions (gh#977). It exists because a <b>host-driven</b> reconnect of
/// the market-data socket does <i>not</i> replay subscriptions — the client only replays on its own internal
/// reconnect — so something must remember what was subscribed, or a recovered socket comes back silent and a hidden
/// stop's promotion stalls with no error anywhere.
/// </summary>
public class TradovateQuoteSubscriptionsTests
{
    private readonly TradovateQuoteSubscriptions _subscriptions = new();

    [Fact]
    public void LiveKeys_ShouldBeEmpty_BeforeAnythingSubscribes()
    {
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public void Track_ShouldExposeTheKeyAsLive_SoAReconnectCanReplayIt()
    {
        _subscriptions.Track("7");

        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public void Release_ShouldReportTheLastHolder_AndDropTheKey()
    {
        _subscriptions.Track("7");

        _subscriptions.Release("7").Should().BeTrue("the last holder is the one that may unsubscribe the wire");
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public void Release_ShouldKeepTheKeyLive_WhileAnotherStreamStillHoldsIt()
    {
        // Two consumers can stream the same contract. Unsubscribing the wire on the FIRST teardown would silently
        // starve the second stream — and a starved quote stream is exactly what stalls stop promotion.
        _subscriptions.Track("7");
        _subscriptions.Track("7");

        _subscriptions.Release("7").Should().BeFalse("a holder remains, so the wire subscription must stay");
        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");

        _subscriptions.Release("7").Should().BeTrue();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public void Release_ShouldReturnFalse_ForAKeyThatWasNeverTracked()
    {
        // An unbalanced release must not drive the count negative, which would make a later real release look like
        // "not the last holder" and leave the wire subscribed forever.
        _subscriptions.Release("7").Should().BeFalse();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public void Release_ShouldNotDriveTheCountNegative_WhenReleasedMoreOftenThanTracked()
    {
        _subscriptions.Track("7");
        _subscriptions.Release("7").Should().BeTrue();
        _subscriptions.Release("7").Should().BeFalse();

        _subscriptions.Track("7");

        _subscriptions.Release("7").Should().BeTrue("the count restarted at one, not below zero");
    }

    [Fact]
    public void Track_ShouldDistinguishKeys()
    {
        _subscriptions.Track("7");
        _subscriptions.Track("8");

        _subscriptions.LiveKeys.Should().BeEquivalentTo(["7", "8"]);
    }

    [Fact]
    public void LiveKeys_ShouldBeASnapshot_SoAReplayIsNotDisturbedByConcurrentTracking()
    {
        // The host enumerates LiveKeys while streams start and stop on other threads; a live view would throw
        // mid-replay and leave the socket half-subscribed.
        _subscriptions.Track("7");
        IReadOnlyList<string> snapshot = _subscriptions.LiveKeys;

        _subscriptions.Track("8");
        _subscriptions.Release("7");

        snapshot.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public void TrackAndRelease_ShouldBalance_UnderConcurrentUse()
    {
        Parallel.For(0, 512, _ =>
        {
            _subscriptions.Track("7");
            _subscriptions.Release("7");
        });

        _subscriptions.LiveKeys.Should().BeEmpty();
    }
}
