using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The account-event subscription supervisor (gh#219): the drop-vs-stop discipline that keeps the stream alive.
/// A session that <b>ends or throws</b> is a drop (re-subscribe after a backoff); <b>cancellation</b> is a stop
/// (propagate, no backoff); teardown (<see cref="ObjectDisposedException"/>) and an unsupported-capability refusal
/// propagate without a retry.
/// </summary>
public class AccountEventSubscriptionSupervisorTests
{
    private int _sessionCalls;
    private int _delayCalls;

    private AccountEventSubscriptionSupervisor Supervisor() => new(
        TimeSpan.FromSeconds(1),
        NullLogger<AccountEventSubscriptionSupervisor>.Instance,
        (_, token) =>
        {
            _delayCalls++;
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

    [Fact]
    public async Task RunAsync_ShouldResubscribe_AfterADrop()
    {
        using CancellationTokenSource cts = new();

        Task Session(CancellationToken token)
        {
            _sessionCalls++;
            if (_sessionCalls == 1)
            {
                throw new InvalidOperationException("dropped");
            }

            cts.Cancel(); // second attempt: stop the loop
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => Supervisor().RunAsync(Session, cts.Token));

        _sessionCalls.Should().Be(2); // retried after the drop
        _delayCalls.Should().Be(1);   // one backoff, after the drop
    }

    [Fact]
    public async Task RunAsync_ShouldTreatACleanStreamEnd_AsADrop_AndResubscribe()
    {
        using CancellationTokenSource cts = new();

        Task Session(CancellationToken token)
        {
            _sessionCalls++;
            if (_sessionCalls >= 2)
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask; // a clean end -- a drop to re-subscribe, not a stop
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => Supervisor().RunAsync(Session, cts.Token));

        _sessionCalls.Should().Be(2);
        _delayCalls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ShouldStopWithoutBackoff_OnCancellation()
    {
        using CancellationTokenSource cts = new();

        Task Session(CancellationToken token)
        {
            _sessionCalls++;
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => Supervisor().RunAsync(Session, cts.Token));

        _sessionCalls.Should().Be(1);
        _delayCalls.Should().Be(0); // a stop never pays the backoff
    }

    [Fact]
    public async Task RunAsync_ShouldRethrowTeardown_WithoutRetry()
    {
        using CancellationTokenSource cts = new();

        Task Session(CancellationToken token)
        {
            _sessionCalls++;
            throw new ObjectDisposedException("provider");
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Supervisor().RunAsync(Session, cts.Token));

        _sessionCalls.Should().Be(1); // teardown is not a transient drop -- no retry
        _delayCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ShouldRethrowUnsupportedCapability_WithoutRetry()
    {
        using CancellationTokenSource cts = new();

        Task Session(CancellationToken token)
        {
            _sessionCalls++;
            throw new VenueCapabilityNotSupportedException(VenueCapability.AccountStreaming);
        }

        await Assert.ThrowsAsync<VenueCapabilityNotSupportedException>(() => Supervisor().RunAsync(Session, cts.Token));

        _sessionCalls.Should().Be(1); // the venue cannot stream -- never retried (R-17)
        _delayCalls.Should().Be(0);
    }
}
