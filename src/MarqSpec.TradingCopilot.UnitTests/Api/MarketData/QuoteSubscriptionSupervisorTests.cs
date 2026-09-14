using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The supervised subscription (gh#13, R-1 / ADR-0013): a venue quote stream is a long-lived thing that
/// <b>drops</b> — reconnects, resets, session expiries — and market data must resume without a human. The
/// supervisor re-subscribes after a drop, backing off between attempts, until it is told to stop; a stop is
/// clean, not an error.
/// </summary>
public class QuoteSubscriptionSupervisorTests
{
    private readonly IEventLog _log = A.Fake<IEventLog>();
    private readonly IMarketDataSource _source = A.Fake<IMarketDataSource>();
    private readonly VenueContractId _contract = VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");
    private readonly List<TimeSpan> _delays = [];

    public QuoteSubscriptionSupervisorTests()
    {
        A.CallTo(() => _source.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));
    }

    private QuoteSubscriptionSupervisor Supervisor(TimeSpan? reconnectDelay = null)
    {
        // Inject a delay hook that records the requested backoff and returns immediately, so the loop's timing
        // is asserted without the test actually waiting.
        return new QuoteSubscriptionSupervisor(
            new QuoteIngestionService(_log),
            reconnectDelay ?? TimeSpan.FromSeconds(5),
            NullLogger<QuoteSubscriptionSupervisor>.Instance,
            delay: (span, _) =>
            {
                _delays.Add(span);
                return Task.CompletedTask;
            });
    }

    private static async IAsyncEnumerable<Quote> OneQuoteThenEnd()
    {
        yield return new Quote(DateTimeOffset.UnixEpoch, new Price(5_300.25m), new Price(5_300.50m), 1, 1);
        await Task.Yield();
    }

    private static async IAsyncEnumerable<Quote> Throwing()
    {
        yield return new Quote(DateTimeOffset.UnixEpoch, new Price(5_300.25m), new Price(5_300.50m), 1, 1);
        await Task.Yield();
        throw new InvalidOperationException("websocket dropped");
    }

    [Fact]
    public async Task RunAsync_ShouldReSubscribe_AfterTheStreamEnds()
    {
        int subscriptions = 0;
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _source.StreamQuotesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueContractId _, CancellationToken __) =>
            {
                // Stop after the third subscription so the supervised loop terminates for the test.
                if (++subscriptions >= 3)
                {
                    cancellation.Cancel();
                }

                return OneQuoteThenEnd();
            });

        Func<Task> act = () => Supervisor().RunAsync(_source, _contract, cancellation.Token);

        // A stream ending is not a failure -- the venue closed a session; the supervisor just re-subscribes.
        await act.Should().ThrowAsync<OperationCanceledException>();
        subscriptions.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_ShouldReSubscribe_AfterTheStreamThrows()
    {
        int subscriptions = 0;
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _source.StreamQuotesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueContractId _, CancellationToken __) =>
            {
                if (++subscriptions >= 2)
                {
                    cancellation.Cancel();
                }

                return Throwing();
            });

        Func<Task> act = () => Supervisor().RunAsync(_source, _contract, cancellation.Token);

        // A drop mid-stream is the common case, and the whole reason the supervisor exists: resume without a human.
        await act.Should().ThrowAsync<OperationCanceledException>();
        subscriptions.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ShouldBackOffBetweenAttempts_ByTheConfiguredDelay()
    {
        int subscriptions = 0;
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _source.StreamQuotesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueContractId _, CancellationToken __) =>
            {
                if (++subscriptions >= 3)
                {
                    cancellation.Cancel();
                }

                return OneQuoteThenEnd();
            });

        await Supervisor(reconnectDelay: TimeSpan.FromSeconds(7))
            .Invoking(supervisor => supervisor.RunAsync(_source, _contract, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // A backoff between every re-subscription -- never a hot loop hammering a downed venue.
        _delays.Should().OnlyContain(delay => delay == TimeSpan.FromSeconds(7));
        _delays.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldStopCleanly_WithoutADelay_WhenCancelledDuringAStream()
    {
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Invokes(() => cancellation.Cancel()) // cancel while ingesting the first quote
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));
        A.CallTo(() => _source.StreamQuotesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .Returns(OneQuoteThenEnd());

        await Supervisor()
            .Invoking(supervisor => supervisor.RunAsync(_source, _contract, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // Cancellation is a stop, not a drop: it must NOT trigger a reconnect backoff.
        _delays.Should().BeEmpty();
    }
}
