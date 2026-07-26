using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Alerting;

/// <summary>
/// Integration coverage for the alerting increment (gh#246, gh#243, ADR-0019, R-13) against real PostgreSQL,
/// with the notification channel behind a recording double so <b>no test can page the operator</b>.
/// </summary>
/// <remarks>
/// <para>
/// Two claims pull in opposite directions and both are load-bearing. <b>It fires when it must</b> — a flatten
/// that fails has to reach a phone. <b>It stays silent when it must</b> — a clean session produces nothing,
/// because a pager that cries on healthy days gets muted, and a muted pager is strictly worse than no pager: it
/// manufactures confidence.
/// </para>
/// <para>
/// Driven through <see cref="AutoFlattenService.RunPassAsync"/> at a controlled instant (the service is
/// clock-free), with the hosted services stripped so the only pass is the test's own — otherwise "exactly one
/// page" cannot be asserted. The dead-man's-switch cases that need an <b>external</b> monitor, above all
/// "the process is killed before the deadline", are staging-tier and are not in this file; see the PR.
/// </para>
/// </remarks>
public class AlertingIntegrationTests : IClassFixture<AlertingTestPostgresFactory>
{
    private readonly AlertingTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Summer date → CDT (UTC−5): the ES deadline of 14:30 CT is 19:30 UTC.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public AlertingIntegrationTests(AlertingTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // -------------------------------------------------------------------------------------------------------
    // It fires
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alert_ShouldPage_WhenFlattenEscalatesAfterMaxAttempts()
    {
        // The condition ADR-0019 says to page on. `missed` at deadline+60min lands AFTER Topstep's ~15:10
        // backstop, so paging on that alone would arrive too late to act.
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25"); // the close never takes → retries → escalation

        await RunFlattenAsync(Utc(19, 45));

        Notifications.Pages.Should().NotBeEmpty("an escalated flatten is the operator's problem now");
    }

    [Fact]
    public async Task Alert_ShouldUsePageSeverity_WhenTheFlattenEscalates()
    {
        // Severity is the difference between a phone that rings through Do Not Disturb and a line in a feed.
        // A P1 delivered as an ordinary notification has failed even though something was "sent".
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);
        VenueFactory.MakeCloseIneffective("ESM25");

        await RunFlattenAsync(Utc(19, 45));

        Notifications.Sent.Should().NotBeEmpty();
        Notifications.Sent.Should().Contain(notification => notification.Severity == NotificationSeverity.Page);
    }

    [Fact]
    public async Task Alert_ShouldCarryADedupKey_WhenItPages()
    {
        // Without a stable key there is no way to send once per incident, and no way to resolve the page later.
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);
        VenueFactory.MakeCloseIneffective("ESM25");

        await RunFlattenAsync(Utc(19, 45));

        Notifications.Pages.Should().OnlyContain(page => !string.IsNullOrWhiteSpace(page.DedupKey));
    }

    // -------------------------------------------------------------------------------------------------------
    // It stays silent
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alert_ShouldProduceNoPush_WhenSessionIsClean()
    {
        // THE most valuable test here. A normal day, a normal flatten, nothing sent. Every false page spends
        // credibility the real one needs.
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);

        await RunFlattenAsync(Utc(19, 45)); // closes cleanly — the stub reports flat afterwards

        Notifications.Sent.Should().BeEmpty("a flatten that worked is not news");
    }

    [Fact]
    public async Task Alert_ShouldProduceNoPush_WhenThereIsNothingToFlatten()
    {
        // The quietest day of all: no position at all. If this pages, every non-trading day pages.
        await FreshAccountAsync();

        await RunFlattenAsync(Utc(19, 45));

        Notifications.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Alert_ShouldProduceNoPush_BeforeTheDeadline()
    {
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);

        await RunFlattenAsync(Utc(19, 15)); // 14:15 CT — fifteen minutes early

        Notifications.Pages.Should().BeEmpty("an open position before its deadline is ordinary trading");
    }

    [Fact]
    public async Task Alert_ShouldSendOnce_WhenTheUnderlyingConditionRepeatsAcrossPasses()
    {
        // The primary re-emits ~every 15s and the watchdog ~every 20s while exposure persists. Without dedup a
        // 30-minute outage is ~120 pushes -- after which the operator mutes the channel, and the next real page
        // is never seen. One incident, one page.
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25");

        await RunFlattenAsync(Utc(19, 45));
        await RunFlattenAsync(Utc(19, 46));
        await RunFlattenAsync(Utc(19, 47));

        Notifications.Pages.Select(page => page.DedupKey).Distinct().Should().HaveCount(1,
            "three passes on the SAME incident are one incident");
        Notifications.Pages.Should().ContainSingle("the channel must send once per incident, not once per pass");
    }

    // -------------------------------------------------------------------------------------------------------
    // It cannot break trading
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alert_ShouldNotFailFlatten_WhenChannelThrows()
    {
        // Alerting is REPORTING. A dead pager must not take the safety net down with it -- that would invert the
        // whole point, turning an observability outage into a trading one (engineering §9).
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);
        Notifications.MakeSendThrow();

        Func<Task> pass = () => RunFlattenAsync(Utc(19, 45));

        await pass.Should().NotThrowAsync("a channel failure must never surface as a flatten failure");
        VenueFactory.ClosePositionCalls.Should().NotBeEmpty("the close still had to happen");
    }

    [Fact]
    public async Task Alert_ShouldNotDelayFlatten_WhenChannelHangs()
    {
        // The gh#289 regression guard, FLIPPED from its pin (QA contract rule 2). It previously asserted the
        // defect -- the send was awaited inline in AutoFlattenService.NotifyAsync, so a 5 s channel made the pass
        // take 5.15 s on the R-13 safety path, contradicting gh#243's own "sending is off the hot path". gh#289
        // put a queue in front of the transport, so the pass now enqueues and returns.
        //
        // Times the PASS ONLY, deliberately not using RunFlattenAsync: that helper drains afterwards, and the
        // drain is where the 5 s legitimately lives. Measuring the two together would re-measure the defect.
        string account = await FreshAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 1);
        VenueFactory.MakeCloseIneffective("ESM25");
        Notifications.MakeSendHang(TimeSpan.FromSeconds(5));

        Stopwatch clock = Stopwatch.StartNew();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
            await service.RunPassAsync(Utc(19, 45), CancellationToken.None);
        }

        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "a hanging channel must not block the flatten pass — the send is enqueued, not awaited");

        // Clear the hang, then drain what this pass left queued. The queue is a singleton for the fixture's
        // lifetime, so a test that enqueues without draining leaks its notifications into the NEXT test -- which
        // is exactly how this one first broke Alert_ShouldProduceNoPush_WhenSessionIsClean.
        Notifications.Reset();
        await _factory.DrainNotificationsAsync();
    }

    // -------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------

    /// <summary>The venue stub's fixed account keys — the dedup memory is keyed on these (see FreshAccountAsync).</summary>
    private static string[] StubAccountKeys { get; } = ["PRAC-50K-101", "50KTC-V2-202", "EXPRESS-50K-303", "UNKNOWN-NAME-999"];

    private RecordingNotificationChannel Notifications => _factory.Notifications;

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>
    /// Runs one flatten pass, then delivers whatever it enqueued.
    /// </summary>
    /// <remarks>
    /// The drain is deliberately AFTER the timed section and is not part of it. Since gh#289 the send is
    /// enqueued rather than awaited, so the pass returns before delivery — which is the whole point, and is what
    /// <see cref="Alert_ShouldNotDelayFlatten_WhenChannelHangs"/> measures. Every other test here asserts on what
    /// was *delivered*, so it has to pump first; with the hosted services removed for determinism, nothing else
    /// will.
    /// </remarks>
    private async Task RunFlattenAsync(DateTimeOffset now)
    {
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
            await service.RunPassAsync(now, CancellationToken.None);
        }

        await _factory.DrainNotificationsAsync();
    }

    /// <summary>Resets the recorder and the venue, clears accounts, and stands up exactly one tradeable account.</summary>
    private async Task<string> FreshAccountAsync()
    {
        // Drain BEFORE resetting: the notification queue is a singleton across the fixture, so anything a
        // previous test enqueued and did not deliver would otherwise arrive mid-test and be attributed here.
        // Draining into the old recorder state and then clearing it leaves this test a genuinely empty slate.
        await _factory.DrainNotificationsAsync();
        Notifications.Reset();
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        // Clear the DEDUP memory between tests, not just the recorder. The deduping decorator is a singleton and
        // the venue stub hands out FIXED account keys, so every test is the same incident (`flatten:<acct>:ES`) --
        // without this, test 1 suppresses every later page and the suite reports silence it did not earn.
        // Resolving is the production re-arm path, so this exercises the real API rather than reaching inside.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            INotificationChannel channel = scope.ServiceProvider.GetRequiredService<INotificationChannel>();
            foreach (string key in StubAccountKeys)
            {
                await channel.ResolveAsync($"flatten:{key}:ES", CancellationToken.None);
            }
        }

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Alert-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).VenueAccountKey;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
