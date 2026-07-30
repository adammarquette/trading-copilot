using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The <b>shape</b> of the notification composition — <b>outbox → queue → dedup → transport</b> (gh#320, gh#437 ⇒
/// gh#289 / gh#400, R-13, ADR-0019). Each link has its own behavioural suite; what nothing guarded is that the
/// composition root actually <i>binds</i> them — and the auto-flatten's protection is entirely a property of that
/// binding, not of anything the flatten service does.
/// </summary>
/// <remarks>
/// <para>
/// <c>AutoFlattenService.NotifyAsync</c> awaits <c>INotificationChannel.SendAsync</c> with the caller's token and has
/// <b>no bound of its own</b> — it catches a channel that <i>throws</i>, not one that <i>hangs</i>. So "a hung channel
/// can never delay a flatten" is true only while the resolved channel returns without touching a network — the queue
/// before gh#437, the outbox after it. Bind a blocking transport there (<see cref="PushoverNotificationChannel"/>
/// carries a 10-second <c>HttpClient</c> timeout) and the R-13 hot path silently regains an unbounded await — at the
/// exact moment a position is already failing to close.
/// </para>
/// <para>
/// Asserted against the <b>real</b> registration rather than a hand-built provider: the invariant is what the
/// composition root does, so a test that wired its own chain would pass while production drifted. Same reasoning as
/// gh#338's exemplar test and gh#382's harness-fidelity guard.
/// </para>
/// </remarks>
public class NotificationRegistrationTests
{
    [Fact]
    public void AddTradingCopilotNotifications_ShouldBindTheChannelToTheOutbox_SoAPageSurvivesACrash()
    {
        // Updated for gh#437, deliberately. This guard previously required the QUEUE at the seam; the outbox now
        // sits outside it, so the assertion moves rather than being deleted. The property it protects is
        // unchanged and still the point: the auto-flatten awaits whatever is bound here on the R-13 hot path.
        //
        // The outbox satisfies that the same way the queue did — it writes one indexed row and returns, never
        // awaiting a network — and adds durability the queue could not: a hard crash before the pump drained lost
        // whatever it held (gh#400).
        using WebApplication app = Compose();

        using IServiceScope scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<INotificationChannel>().Should().BeOfType<OutboxNotificationChannel>(
            "a page must be durable before the caller is told it succeeded — binding the queue or a transport "
            + "directly re-creates gh#289/gh#400 with every test still green");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldBindTheEnlisterToTheSameInstanceAsTheSeam_SoTheRowRidesTheCallersSave()
    {
        // gh#455's load-bearing binding, and the one way this can fail silently. Enlist stages a row into the
        // DbContext of the instance it is called on; the producer then saves through ITS context. If the enlister
        // resolved to a DIFFERENT instance — a second registration, or a transient — the row would be staged into a
        // context nobody saves, every unit test would still pass, and the flatten's page would simply never exist.
        //
        // Identity is asserted, not just the type: two OutboxNotificationChannel instances would satisfy a type
        // check and still lose the page.
        using WebApplication app = Compose();

        using IServiceScope scope = app.Services.CreateScope();

        INotificationEnlister enlister = scope.ServiceProvider.GetRequiredService<INotificationEnlister>();
        INotificationChannel channel = scope.ServiceProvider.GetRequiredService<INotificationChannel>();

        enlister.Should().BeSameAs(channel,
            "Enlist stages into the context of whichever instance it is called on — a second instance stages into "
            + "a context nobody saves, and the page vanishes with every test still green");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldScopeTheEnlister_SoItSharesTheProducersUnitOfWork()
    {
        // Scoped, necessarily: the whole guarantee is that the row joins the producer's transaction, and a
        // singleton would capture one context for the process — the captive-dependency shape that also throws
        // under ValidateScopes at startup.
        using WebApplication app = Compose();

        using IServiceScope first = app.Services.CreateScope();
        using IServiceScope second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<INotificationEnlister>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<INotificationEnlister>(),
                "each producer pass has its own unit of work, so it must have its own enlister");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldKeepTheQueueBeneathTheOutbox_SoDeliveryStillNeverBlocks()
    {
        // The other half of the same property. The outbox is durable but it does not deliver; the relay does, and
        // the relay must hand off to something that returns immediately. If the queue were removed from beneath
        // it, the transport's latency would land on the relay pass instead — off the flatten path, but still a
        // single wedged network call stalling every page behind it.
        using WebApplication app = Compose();

        app.Services.GetRequiredService<QueuedNotificationChannel>().Should().NotBeNull(
            "the relay delivers through the queue, so the chain below the outbox must still be queue → dedup → transport");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterThePump_SoAnEnqueuedPageIsNotSilentlyDropped()
    {
        // Enqueue-and-return is only SAFE because something drains the queue. Drop the pump and SendAsync still
        // reports success on every page while none is ever delivered — a silent failure on the one alert that wakes
        // the operator, and invisible from the caller's side.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotNotifications();

        // Reduced to a bool before asserting: `Should().Contain(predicate)` on a service collection renders the
        // entire container in its failure message, which buries the one fact that matters.
        bool pumpRegistered = builder.Services.Any(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(NotificationPumpHost));

        pumpRegistered.Should().BeTrue("a queue nobody drains accepts every page and delivers none");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldKeepTheDedupChainASingleton_SoItRemembersTheOpenIncident()
    {
        // Updated for gh#437, and the reasoning is why it could not simply be deleted. The seam itself is now
        // SCOPED — the outbox writes through a scoped DbContext — so asserting the seam is a singleton would be
        // wrong. But the property the old assertion protected is unchanged: the dedup decorator holds the
        // open-incident set IN MEMORY, and a scoped one would forget it between passes, turning "one page per
        // incident" into one page per 15-second poll. That is the alert fatigue ADR-0019 exists to prevent.
        //
        // So the assertion moves down a layer, to the object that actually holds the state.
        using WebApplication app = Compose();

        using IServiceScope first = app.Services.CreateScope();
        using IServiceScope second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<QueuedNotificationChannel>().Should().BeSameAs(
            second.ServiceProvider.GetRequiredService<QueuedNotificationChannel>(),
            "dedup state lives beneath the queue, so two passes must reach the same instance");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterTheRelay_SoADurableRowIsNotSilentlyNeverSent()
    {
        // The pump guard's twin, one layer out (gh#437). A row persisted and never read is worse than not
        // persisting it: the caller was told the page succeeded, so the failure is invisible from every side —
        // no exception, no queue backlog, just a table quietly filling with pages nobody was ever sent.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotNotifications();

        bool relayRegistered = builder.Services.Any(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(NotificationOutboxRelayHost));

        relayRegistered.Should().BeTrue("an outbox nobody drains records every page and delivers none");
    }

    /// <summary>Builds a host with the <b>real</b> notification registration, plus the one dependency it now has.</summary>
    /// <remarks>
    /// The database is registered because gh#437 made the seam durable: <see cref="OutboxNotificationChannel"/>
    /// writes through the scoped <c>DbContext</c>, so resolving the seam needs one present. An in-memory provider
    /// is right here — these cases assert the <b>shape of the composition</b>, never storage behaviour, and the
    /// registration under test is still the production one.
    /// </remarks>
    private static WebApplication Compose()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<ICurrentUser>(_ => new FixedUser(Guid.Empty));
        builder.Services.AddDbContext<TradingCopilotDbContext>(
            options => options.UseInMemoryDatabase($"notification-registration-{Guid.NewGuid()}"));
        builder.AddTradingCopilotNotifications();
        return builder.Build();
    }

    private sealed record FixedUser(Guid UserId) : ICurrentUser;
}
