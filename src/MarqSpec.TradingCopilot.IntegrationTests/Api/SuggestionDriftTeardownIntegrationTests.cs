using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The drift consumer's <b>teardown</b> half (gh#632 ⇒ gh#546's DoD, ADR-0013): the live
/// <see cref="SuggestionDriftHost"/> must exit cleanly on host shutdown rather than joining the gh#169
/// <c>ObjectDisposedException</c> cascade a missing catch would leave behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own class, and its own fixture, deliberately</b> (gh#632 review).
/// <see cref="SuggestionDriftConsumerLifecycleIntegrationTests"/> runs on a factory that keeps every always-on
/// hosted service alive — correct for proving an event travels end to end, wrong for a lifecycle assertion. There,
/// a second <see cref="SuggestionDriftHost"/> owned by the class fixture polls and commits the <i>same</i>
/// <c>suggestion-drift</c> cursor on the <i>same</i> database as the host being torn down, so its contention and
/// its log lines land inside the exact window under test — failing this for a reason unrelated to gh#169, or
/// masking a genuine regression behind another host's traffic.
/// </para>
/// <para>
/// <see cref="SuggestionDriftTeardownTestPostgresFactory"/> therefore runs <b>no</b> hosted services in the fixture
/// host and adds back exactly one — the consumer under test — in
/// <see cref="SuggestionDriftTeardownTestPostgresFactory.CreateDriftOnlyHost"/>. Nothing else polls, nothing else
/// can log, and this test owns the only lifetime in play.
/// </para>
/// </remarks>
public class SuggestionDriftTeardownIntegrationTests : IClassFixture<SuggestionDriftTeardownTestPostgresFactory>
{
    private readonly SuggestionDriftTeardownTestPostgresFactory _factory;

    public SuggestionDriftTeardownIntegrationTests(SuggestionDriftTeardownTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Host_ShouldExitCleanly_OnTeardown_WithoutAnUncaughtFailure()
    {
        // An independently-lifetimed host (the "restart" idiom other suites use, e.g. StateRehydrationIntegrationTests'
        // CreateHost) — so this test controls exactly when SuggestionDriftHost tears down, rather than depending on a
        // shared fixture's disposal timing at the end of the run. Here it is also the ONLY drift consumer running.
        _factory.Logs.Clear();
        WebApplicationFactory<Program> restarted = _factory.CreateDriftOnlyHost();

        // Let the host actually enter its poll loop (IdlePoll = 1s) before tearing down — the interesting case is
        // stopping a host that is genuinely mid-loop, not one that never started. CreateClient() is what forces the
        // otherwise-deferred host to build and start (the same nudge StateRehydrationIntegrationTests uses).
        _ = restarted.CreateClient();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Task disposeTask = restarted.DisposeAsync().AsTask();
        Task first = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(20)));
        first.Should().Be(disposeTask, "teardown must complete promptly — a hang means the host is not exiting cleanly (gh#169); " +
            "a missing OperationCanceledException/ObjectDisposedException catch is exactly the defect this guards");
        await disposeTask; // re-observe: if disposal itself faulted, this rethrows and fails the test

        // SCOPED TO THE HOST UNDER TEST (gh#632 review). The generic host logs an unhandled BackgroundService failure
        // at Error severity (distinct from this host's own LogWarning on a transient DB/venue fault mid-pass) — the
        // observable trace an uncaught ObjectDisposedException on shutdown would leave. An unscoped "nothing at Error"
        // would also be failed by any other component's Error, which would make a red here mean something other than
        // the defect. Both categories that can carry this host's failure are allowed for: its own, and the generic
        // host's BackgroundService handler, which names the failing service in the message.
        IReadOnlyList<CapturedLog> errors = [.. _factory.Logs.Records.Where(entry => entry.Level == LogLevel.Error)];

        errors.Should().NotContain(
            entry => entry.Category == typeof(SuggestionDriftHost).FullName
                || entry.Message.Contains(nameof(SuggestionDriftHost), StringComparison.Ordinal),
            "a clean shutdown logs nothing at Error severity for this host — an uncaught exception from a missing "
            + "OperationCanceledException/ObjectDisposedException catch would surface as exactly that (gh#169)");
    }
}
