using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
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
    : IClassFixture<NotificationDurabilityTestFactory>, IClassFixture<AgentReviewTestPostgresFactory>
{
    private readonly NotificationDurabilityTestFactory _durability;
    private readonly AgentReviewTestPostgresFactory _agentReview;

    public NotificationHarnessFidelityTests(
        NotificationDurabilityTestFactory durability, AgentReviewTestPostgresFactory agentReview)
    {
        _durability = durability;
        _agentReview = agentReview;
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
