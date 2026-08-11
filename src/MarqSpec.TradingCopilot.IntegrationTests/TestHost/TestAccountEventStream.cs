using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The adversarial <see cref="IAccountEventStream"/> double for gh#779: feeds exactly the events a test arms,
/// in the order armed, and stays open afterward rather than ending the enumerable. It never decides how
/// <c>AccountEventStreamHost</c> reacts to what it feeds — it only supplies the venue-neutral events a real
/// ProjectX stream would deliver, the same adversarial discipline <see cref="AdversarialTestTradingVenue"/>
/// follows for order/position/fill reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a channel, not a fixed list.</b> A session that ENDS reads as a <b>drop</b> to
/// <c>AccountEventSubscriptionSupervisor</c>, which logs and re-subscribes after its reconnect delay — noise a
/// deterministic test does not want. A channel lets a test arm a <b>second</b> event only after observing the
/// first one's effects (e.g. after an induced fault clears), so it can prove the host kept consuming the stream
/// rather than merely that it did not crash before the test stopped it anyway.
/// </para>
/// <para>Not thread-hostile, but not designed for concurrent test authors either: one instance per fixture,
/// <see cref="Reset"/> at the start of every test so one test's events can never leak into another's.</para>
/// </remarks>
internal sealed class TestAccountEventStream : IAccountEventStream
{
    private Channel<AccountEvent> _channel = Channel.CreateUnbounded<AccountEvent>();

    /// <inheritdoc />
    public VenueId Venue => VenueId.Parse("projectx");

    /// <summary>Replaces the channel with a fresh, empty one — call before arming a new test's events.</summary>
    public void Reset() => _channel = Channel.CreateUnbounded<AccountEvent>();

    /// <summary>Enqueues events for the next (or an already-running) <see cref="StreamAsync"/> call to yield.</summary>
    /// <param name="events">The venue-neutral events to feed, oldest first.</param>
    public void Arm(params AccountEvent[] events)
    {
        foreach (AccountEvent accountEvent in events)
        {
            _channel.Writer.TryWrite(accountEvent);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AccountEvent> StreamAsync(
        IReadOnlyCollection<VenueAccountId> accounts, CancellationToken cancellationToken) =>
        ReadAsync(_channel, cancellationToken);

    private static async IAsyncEnumerable<AccountEvent> ReadAsync(
        Channel<AccountEvent> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (AccountEvent accountEvent in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return accountEvent;
        }
    }
}
