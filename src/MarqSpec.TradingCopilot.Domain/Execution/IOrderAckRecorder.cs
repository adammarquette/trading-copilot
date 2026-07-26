namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Records how long a venue order transmit took to acknowledge (gh#295, gh#232) — the <c>trading.order.ack_latency</c>
/// SLI. Declared in the Domain so <see cref="OrderExecutionService"/> can time the true venue round-trip at the
/// boundary where it happens, while the concrete metrics sink stays in the Api layer (the Domain references no Api).
/// </summary>
/// <remarks>
/// Implementations must be <b>total</b> — recording a measurement can never fail or slow a trade (engineering §9).
/// The no-op default lets a caller that has no sink wired (a unit test constructing the service directly) run
/// without one.
/// </remarks>
public interface IOrderAckRecorder
{
    /// <summary>A sink that records nothing — the default when no metrics pipeline is present.</summary>
    public static IOrderAckRecorder None { get; } = new NoOrderAckRecorder();

    /// <summary>Records the transmit → venue-acknowledgement latency of one successful placement.</summary>
    /// <param name="elapsed">The venue round-trip.</param>
    void RecordOrderAck(TimeSpan elapsed);

    private sealed class NoOrderAckRecorder : IOrderAckRecorder
    {
        public void RecordOrderAck(TimeSpan elapsed)
        {
            // Intentionally empty — the null-object sink.
        }
    }
}
