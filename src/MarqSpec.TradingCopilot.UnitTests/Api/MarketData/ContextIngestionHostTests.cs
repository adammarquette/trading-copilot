using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The cross-asset context ingestion host and its supervisor (gh#496, of gh#411).
/// </summary>
/// <remarks>
/// The case that carries this card's sharpest acceptance criterion — <i>a Finnhub outage must not disturb ProjectX
/// futures ingestion</i> — is <see cref="ExecuteAsync_ShouldSwallowAFailureBuildingTheSource_SoAnOptionalFeedCannotStopTheWholeHost"/>.
/// A <c>BackgroundService</c> whose <c>ExecuteAsync</c> throws stops the <b>entire application</b> under the
/// default <c>BackgroundServiceExceptionBehavior.StopHost</c>, so an uncaught failure here would take the quote
/// feed, the auto-flatten watchdog and the kill switch down with it — because an optional colour feed was
/// misconfigured.
/// </remarks>
public class ContextIngestionHostTests
{
    private static VenueContractId Contract => VenueContractId.Create(VenueId.Parse("finnhub"), "SPY");

    private static ContextIngestionHost Host(IServiceProvider services, params string[] symbols) =>
        new(services,
            Options.Create(new ContextIngestionOptions { Symbols = symbols, ReconnectDelay = TimeSpan.Zero }),
            NullLogger<ContextIngestionHost>.Instance);

    [Fact]
    public async Task ExecuteAsync_ShouldSwallowAFailureBuildingTheSource_SoAnOptionalFeedCannotStopTheWholeHost()
    {
        // A missing Finnhub token is enough to throw while the source is constructed. If that escaped, .NET's
        // default StopHost behaviour would stop the platform — trading included — over an absent colour feed.
        ServiceCollection services = new();
        services.AddSingleton<IContextMarketDataSource>(_ =>
            throw new InvalidOperationException("Finnhub API token is not configured (test)."));
        ContextIngestionHost host = Host(services.BuildServiceProvider(), "SPY");

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync(
            "an optional context feed failing must degrade to no context data, never stop the host");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDoNothing_WhenNoSymbolsAreConfigured()
    {
        // Opt-in: a deployment with no Finnhub token simply leaves the list unset, and must not be warned at or
        // have a source resolved for it.
        ServiceCollection services = new();
        services.AddSingleton<IContextMarketDataSource>(_ => throw new InvalidOperationException("must not resolve"));
        ContextIngestionHost host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync("with no symbols the host returns before touching the source");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenSymbolsAreConfiguredButNoSourceIsRegistered()
    {
        ContextIngestionHost host = Host(new ServiceCollection().BuildServiceProvider(), "SPY");

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Supervisor_ShouldReSubscribe_WhenTheContextStreamDrops()
    {
        // A dropped context stream is routine — a rate limit, a socket reset, a provider outage. The supervisor
        // must treat it as a drop (back off, re-subscribe), never let it out.
        IEventLog log = A.Fake<IEventLog>();
        IContextMarketDataSource source = A.Fake<IContextMarketDataSource>();
        A.CallTo(() => source.Id).Returns(VenueId.Parse("finnhub"));

        int attempts = 0;
        A.CallTo(() => source.StreamContextTradesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                attempts++;
                return Throwing();
            });

        using CancellationTokenSource cancellation = new();
        ContextSubscriptionSupervisor supervisor = new(
            new ContextTradeIngestionService(log),
            TimeSpan.Zero,
            NullLogger<ContextSubscriptionSupervisor>.Instance,
            // Stop after a few reconnects, so the loop is bounded without waiting.
            (_, _) =>
            {
                if (attempts >= 3)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            });

        Func<Task> run = () => supervisor.RunAsync(source, Contract, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>("cancellation is a stop, and it propagates");
        attempts.Should().BeGreaterThan(1, "a dropped stream must be re-subscribed, not abandoned");
    }

    [Fact]
    public async Task Supervisor_ShouldStopImmediately_WhenCancelledWithoutBackingOff()
    {
        IEventLog log = A.Fake<IEventLog>();
        IContextMarketDataSource source = A.Fake<IContextMarketDataSource>();
        A.CallTo(() => source.Id).Returns(VenueId.Parse("finnhub"));

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        bool backedOff = false;
        ContextSubscriptionSupervisor supervisor = new(
            new ContextTradeIngestionService(log),
            TimeSpan.FromMinutes(5),
            NullLogger<ContextSubscriptionSupervisor>.Instance,
            (_, _) =>
            {
                backedOff = true;
                return Task.CompletedTask;
            });

        Func<Task> run = () => supervisor.RunAsync(source, Contract, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        backedOff.Should().BeFalse("a stop must never cost a reconnect delay");
    }

    private static async IAsyncEnumerable<ContextTrade> Throwing()
    {
        await Task.Yield();
        throw new InvalidOperationException("context stream dropped (test)");
#pragma warning disable CS0162 // Unreachable — the compiler needs a yield to make this an iterator.
        yield break;
#pragma warning restore CS0162
    }
}
