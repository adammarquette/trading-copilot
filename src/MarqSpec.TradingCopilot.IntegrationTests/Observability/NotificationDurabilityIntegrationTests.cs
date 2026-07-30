using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Integration coverage for the <b>durable notification seam</b> (gh#442 ⇒ gh#400/gh#437, R-13, ADR-0019) — the
/// guarantee that a page survives the process that raised it.
/// </summary>
/// <remarks>
/// <para>
/// This is the coverage gh#442 found missing. Two harnesses had deleted the whole <see cref="INotificationChannel"/>
/// registration and rebuilt the pre-outbox chain, so the outbox and its relay were not in the composition under
/// test at all — durability was uncovered and nothing failed. These cases run on
/// <see cref="NotificationHarnessPostgresFactory"/>, which substitutes only the <b>wire</b>, so the outbox, the
/// relay, the queue, the dedup decorator and the real Pushover adapter are all production objects.
/// </para>
/// <para>
/// <b>These cases are gh#459's regression guard.</b> Putting the outbox back into the chain immediately exposed
/// that <c>AddScoped&lt;NotificationOutboxRelay&gt;()</c> resolved its <c>INotificationChannel delivery</c> to the
/// scoped <c>OutboxNotificationChannel</c> — the very thing it drains — so a page was marked handled having reached
/// nothing, R-13 escalation included. They were pinned to that observed behaviour per the QA contract until the fix
/// landed (gh#468), and now assert the real guarantee: <b>a page reaches the transport exactly once</b>.
/// </para>
/// <para>
/// Note the two-step delivery. <c>RelayOutboxAsync</c> only moves a row into the <b>queue</b>; with the hosted
/// services stripped nothing pumps it, so a wire assertion needs <c>DeliverOutboxAsync</c> (relay → queue → pump).
/// A wire assertion after a bare relay pass is vacuous — it holds whatever the system does.
/// </para>
/// </remarks>
public class NotificationDurabilityIntegrationTests : IClassFixture<NotificationDurabilityTestFactory>
{
    private readonly NotificationDurabilityTestFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public NotificationDurabilityIntegrationTests(NotificationDurabilityTestFactory factory)
    {
        _factory = factory;
    }

    private static Notification Page(string dedupKey) =>
        new(NotificationSeverity.Page, "Flatten escalation", "ES is still open past its deadline.", dedupKey);

    [Fact]
    public async Task Outbox_ShouldPersistThePage_BeforeAnythingIsDelivered()
    {
        await ResetAsync();

        await SendAsync(Page("durability:persist:1"));

        // Read through a FRESH scope: this proves a COMMIT, not a tracked entity sitting in the sender's context.
        NotificationOutboxRecord row = await _factory.WithDatabaseAsync(db =>
            db.NotificationOutbox.AsNoTracking().SingleAsync());
        row.DeliveredAt.Should().BeNull("the page is owed until a relay pass delivers it");
        row.Attempts.Should().Be(0, "nothing has been attempted yet");
        _factory.Pushover.Sent.Should().BeEmpty(
            "persistence happens BEFORE delivery — that ordering is the whole point of the outbox (gh#400)");
    }

    [Fact]
    public async Task Outbox_ShouldHoldOneRow_WhenTheSameIncidentRepeats()
    {
        await ResetAsync();

        await SendAsync(Page("durability:repeat:1"));
        await SendAsync(Page("durability:repeat:1"));
        await SendAsync(Page("durability:repeat:1"));

        int rows = await _factory.WithDatabaseAsync(db => db.NotificationOutbox.CountAsync());
        rows.Should().Be(1,
            "one OPEN incident is one row — a unique index on DedupKey FILTERED to DeliveredAt IS NULL (gh#458), "
            + "enforced by real Postgres and invisible to an in-memory provider");
    }

    [Fact]
    public async Task Outbox_ShouldWithdrawTheRow_WhenTheIncidentClearsBeforeAnyRelayPass()
    {
        await ResetAsync();
        await SendAsync(Page("durability:withdraw:1"));

        await ResolveAsync("durability:withdraw:1");

        int rows = await _factory.WithDatabaseAsync(db => db.NotificationOutbox.CountAsync());
        rows.Should().Be(0, "a condition that cleared before its page went out should leave nothing owed");
        _factory.Pushover.Sent.Should().BeEmpty("and should never page at all");
    }

    [Fact]
    public async Task Outbox_ShouldSurviveTheProcessThatRaisedIt_AndBeRelayedLater()
    {
        await ResetAsync();
        // A row "left by a previous life": committed directly, as a crashed process would have left it. This is the
        // crash-survival claim, and it is inexpressible against an in-process queue — the committed row is the only
        // carrier of the intent.
        await _factory.WithDatabaseAsync(async db =>
        {
            db.NotificationOutbox.Add(new NotificationOutboxRecord
            {
                Id = Guid.CreateVersion7(),
                DedupKey = "durability:previous-life:1",
                Severity = NotificationSeverity.Page,
                Title = "Left by a dead process",
                Body = "This page outlived the host that raised it.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                DeliveredAt = null,
                Attempts = 0,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        // The FULL delivery path, not just the relay: the relay hands to the queue, and with the hosts stripped
        // nothing pumps it. Relaying alone could never put anything on the wire, so a wire assertion after
        // RelayOutboxAsync() passes whatever the system does — which is exactly how this guard was vacuous while it
        // carried a gh#459 pin.
        await _factory.DeliverOutboxAsync();

        // The row IS picked up — the host reads an owed row it never wrote, which is the durability claim itself.
        NotificationOutboxRecord afterRelay = await _factory.WithDatabaseAsync(db =>
            db.NotificationOutbox.AsNoTracking().SingleAsync());
        afterRelay.Attempts.Should().BeGreaterThan(0, "the relay found the orphaned row and tried to deliver it");
        afterRelay.DeliveredAt.Should().NotBeNull("and delivered it, stamping the durable record");
        _factory.Pushover.Sent.Should().ContainSingle(
            "a page left by a dead process reaches the transport once a later host relays it — the crash-survival "
            + "claim, end to end (gh#459 regression guard)");
    }

    [Fact]
    public async Task Outbox_ShouldStampDelivered_WhenTheRelayPassRuns()
    {
        await ResetAsync();
        await SendAsync(Page("durability:deliver:1"));

        await _factory.DeliverOutboxAsync();

        NotificationOutboxRecord row = await _factory.WithDatabaseAsync(db =>
            db.NotificationOutbox.AsNoTracking().SingleAsync());
        row.Attempts.Should().BeGreaterThan(0, "the attempt is counted, so a wedged transport is visible");
        row.DeliveredAt.Should().NotBeNull("the stamp is the durable record that the page was handed on");

        // The gh#459 regression guard. Until that fix, the relay's `delivery` resolved to the OutboxNotificationChannel
        // it was draining, so a page was marked handled having reached nothing — invisible, because the outbox table
        // looked perfectly healthy. Delivering into the QUEUE is what puts it on the wire.
        _factory.Pushover.Pages.Should().ContainSingle(
            "the relay delivers into the queue, so an Emergency page reaches the transport exactly once (gh#459)");
    }

    // =============================================================================================================
    // gh#481 — the dedup key's LIFETIME. Suppression must expire with the incident, not outlive it.
    // =============================================================================================================

    [Fact]
    public async Task Outbox_ShouldRecordASecondOccurrence_WhenTheFirstWasAlreadyDelivered()
    {
        await ResetAsync();
        const string incident = "flatten:PRAC-50K-101:ES";

        // Monday's escalation: raised, relayed, delivered.
        await SendAsync(Page(incident));
        await _factory.DeliverOutboxAsync();

        // Tuesday's IDENTICAL escalation. The key is the same because the incident is the same — that is the point
        // of a stable dedup key — but the first occurrence is long delivered, so this one is a new page.
        await SendAsync(Page(incident));
        await _factory.DeliverOutboxAsync();

        int rows = await _factory.WithDatabaseAsync(db => db.NotificationOutbox.CountAsync());
        rows.Should().Be(2,
            "a delivered incident releases its key at the OUTBOX — the second occurrence is recordable (gh#458)");

        // DEFECT gh#497: only the FIRST page reaches the wire. gh#458 released the key at the outbox, but
        // DedupingNotificationChannel — a singleton one layer downstream — still holds it, and its `_reported` set
        // is cleared ONLY by ResolveAsync (no TTL, no eviction: "cleared by ResolveAsync, so no eviction policy is
        // needed"). So when an incident ends WITHOUT a resolve — the operator closes the position by hand, so no
        // flatten pass ever observes flat-with-a-position-due — the key stays armed and the next genuine
        // escalation is suppressed as a stale duplicate.
        //
        // Pins OBSERVED behaviour per the QA contract. When #489 lands this flips to HaveCount(2) and becomes its
        // regression guard. See Outbox_ShouldPageAgain_WhenTheIncidentResolvedBetweenOccurrences below, which
        // proves the suppressor IS the decorator: identical steps plus a resolve, and both pages land.
        _factory.Pushover.Pages.Should().ContainSingle(
            "DEFECT gh#497 — the operator is NOT paged for the second failure, the exact silence gh#481 set out "
            + "to remove: a repeat R-13 escalation unreportable precisely because the first one was reported");
    }

    [Fact]
    public async Task Outbox_ShouldPageAgain_WhenTheIncidentResolvedBetweenOccurrences()
    {
        await ResetAsync();
        const string incident = "flatten:PRAC-50K-101:GC";

        await SendAsync(Page(incident));
        await _factory.DeliverOutboxAsync();

        // The incident clears — the flatten's own success path (FlattenVerdict.Flat) does exactly this — and the
        // same condition recurs later.
        await ResolveAsync(incident);
        await SendAsync(Page(incident));
        await _factory.DeliverOutboxAsync();

        _factory.Pushover.Pages.Should().HaveCount(2,
            "a resolved incident that recurs is a NEW incident and must page again. This is the counterpart to "
            + "gh#497: the resolve is the ONLY thing that re-arms the dedup decorator, which is why an incident "
            + "that ends without one silences its own successor");
    }

    [Fact]
    public async Task Outbox_ShouldStillSuppress_WhileTheFirstOccurrenceIsStillOwed()
    {
        await ResetAsync();
        const string incident = "flatten:PRAC-50K-101:NQ";

        // The same incident twice with NO relay pass between, and — critically — from two SEPARATE scopes, so the
        // change-tracker's Local set cannot be what suppresses the second. This pins the DATABASE-side guard (and
        // the filtered unique index behind it), which an in-memory provider cannot witness at all.
        await SendAsync(Page(incident));
        await SendAsync(Page(incident));

        int owed = await _factory.WithDatabaseAsync(db =>
            db.NotificationOutbox.CountAsync(row => row.DeliveredAt == null));
        owed.Should().Be(1, "while an incident is still owed, a repeat adds nothing — one incident, one page");
        _factory.Pushover.Sent.Should().BeEmpty("and nothing has been delivered yet");
    }

    [Fact]
    public async Task Enlist_ShouldNotAbortTheProducersSave_WhenTheIncidentIsAlreadyOwed()
    {
        await ResetAsync();
        string account = await FreshFlattenAccountAsync();
        VenueFactory.SeedPosition(account, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25"); // never closes → retries → escalation

        // Two consecutive passes on the same unresolved incident, with no relay between — so the first occurrence
        // is still owed when the second is staged.
        //
        // Why the owed-check is load-bearing rather than an optimisation: EnlistAsync only STAGES the row, and the
        // flatten's own JournalAsync is what saves. So a duplicate does not fault at the enlist — it faults at the
        // FLATTEN's save, which is outside the gh#455 try/catch that guards EnlistPageAsync. That belt absorbs a
        // throwing channel; it cannot absorb a violation raised after it returns. The escalation record would go
        // down with the page — the alert destroying the record it exists to report.
        await RunFlattenPassAsync(FlattenInstant);
        await RunFlattenPassAsync(FlattenInstant.AddMinutes(1));

        int escalations = await _factory.WithDatabaseAsync(db =>
            db.Events.CountAsync(candidate => candidate.Type == "flatten.escalated"));
        escalations.Should().Be(2,
            "BOTH passes must be journalled. Without the owed-check the second pass's duplicate aborted the "
            + "producer's save, losing the escalation record — the flatten failure went unrecorded because it had "
            + "already been reported once (gh#455)");

        int owed = await _factory.WithDatabaseAsync(db =>
            db.NotificationOutbox.CountAsync(row => row.DeliveredAt == null));
        owed.Should().Be(1, "and the incident is still one incident — the second pass added no second page");
    }

    private async Task ResetAsync()
    {
        _factory.Pushover.Reset();
        await _factory.ClearOutboxAsync();
    }

    private async Task SendAsync(Notification notification)
    {
        // The seam is SCOPED (it owns a DbContext), so it must be resolved inside a scope — resolving from the root
        // provider throws under scope validation.
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INotificationChannel channel = scope.ServiceProvider.GetRequiredService<INotificationChannel>();
        await channel.SendAsync(notification, CancellationToken.None);
    }

    private async Task ResolveAsync(string dedupKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INotificationChannel channel = scope.ServiceProvider.GetRequiredService<INotificationChannel>();
        await channel.ResolveAsync(dedupKey, CancellationToken.None);
    }

    // -------------------------------------------------------------------------------------------------------
    // The flatten producer path — only gh#481 case 3 needs it. It is the one case whose subject is a REAL
    // producer enlisting into its own transaction, which is the shape gh#455 was about; the other two drive the
    // channel directly because the key's lifetime is the whole subject there.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Summer date → CDT (UTC−5), so ES's 14:30 CT deadline is 19:30 UTC and 19:45 is past it.</summary>
    private static DateTimeOffset FlattenInstant { get; } = new(2026, 7, 15, 19, 45, 0, TimeSpan.Zero);

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task RunFlattenPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    /// <summary>Stands up one tradeable account and clears the state that leaks across a shared fixture.</summary>
    private async Task<string> FreshFlattenAccountAsync()
    {
        VenueFactory.ResetPositions();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Events.ExecuteDeleteAsync(); // the escalation COUNT is the assertion — start from zero
        }

        HttpClient client = _factory.CreateClient();
        using (HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword)))
        {
            LoginTokenResponse? auth = await login.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
            ArgumentNullException.ThrowIfNull(auth);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        }

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Durability-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).VenueAccountKey;
    }

    private sealed record LoginTokenResponse(string Token);
}

/// <summary>A plain notification harness — no suite-specific doubles; the chain itself is the subject (gh#442).</summary>
public sealed class NotificationDurabilityTestFactory : NotificationHarnessPostgresFactory
{
}

/// <summary>
/// The gh#442 fidelity guard, stated in CI's language: each notification harness must still compose <b>production's</b>
/// chain. If a future change moves the seam — or a harness rebuilds it — this fails by name rather than as a cryptic
/// throw inside somebody's fixture.
/// </summary>
public class NotificationHarnessFidelityTests
    : IClassFixture<NotificationDurabilityTestFactory>,
      IClassFixture<AgentReviewTestPostgresFactory>,
      IClassFixture<AlertingTestPostgresFactory>
{
    private readonly NotificationDurabilityTestFactory _durability;
    private readonly AgentReviewTestPostgresFactory _agentReview;
    private readonly AlertingTestPostgresFactory _alerting;

    public NotificationHarnessFidelityTests(
        NotificationDurabilityTestFactory durability,
        AgentReviewTestPostgresFactory agentReview,
        AlertingTestPostgresFactory alerting)
    {
        _durability = durability;
        _agentReview = agentReview;
        _alerting = alerting;
    }

    [Fact]
    public void AlertingHarness_ShouldComposeProductionsNotificationChain()
    {
        // The last host to migrate (gh#480). It was the original gh#442 offender and stayed on the rebuilt chain
        // while gh#459 was open; with all three covered here, no notification harness is outside the guard.
        Action assert = _alerting.AssertProductionChainIntact;
        assert.Should().NotThrow("every notification harness must boot production's chain, not a rebuild");
    }

    [Fact]
    public void DurabilityHarness_ShouldComposeProductionsNotificationChain()
    {
        Action assert = _durability.AssertProductionChainIntact;
        assert.Should().NotThrow("the harness must boot production's outbox → queue → dedup chain, not a rebuild");
    }

    [Fact]
    public void AgentReviewHarness_ShouldComposeProductionsNotificationChain()
    {
        // This one is the regression guard for the gh#442 defect itself: this factory used to rebuild the chain.
        Action assert = _agentReview.AssertProductionChainIntact;
        assert.Should().NotThrow("a suite host must substitute the transport, never the seam");
    }
}
