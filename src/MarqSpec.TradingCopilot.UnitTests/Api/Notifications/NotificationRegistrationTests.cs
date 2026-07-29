using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The <b>shape</b> of the notification composition (gh#320 ⇒ gh#289, gh#400, R-13, ADR-0019). The pieces have
/// their own behavioural suites; what nothing else guards is that the composition root actually <i>binds</i> them
/// — and the auto-flatten's protection is entirely a property of that binding, not of anything the flatten
/// service does.
/// </summary>
/// <remarks>
/// <para>
/// <c>AutoFlattenService.NotifyAsync</c> awaits <c>INotificationChannel.SendAsync</c> with the caller's token and
/// has <b>no bound of its own</b> — it catches a channel that <i>throws</i>, not one that <i>hangs</i>. So "a hung
/// channel can never delay a flatten" is true only while the resolved channel does no network work. Bind a
/// transport there (<see cref="PushoverNotificationChannel"/> carries a 10-second <c>HttpClient</c> timeout) and
/// the R-13 hot path silently regains an unbounded await — at the exact moment a position is already failing to
/// close.
/// </para>
/// <para>
/// <b>gh#400 moved which class holds each property, so these guards moved with it.</b> The seam used to be the
/// in-process queue, registered as a singleton. It is now the durable <see cref="OutboxNotificationChannel"/>,
/// which must be <b>scoped</b> to write through the scoped <c>DbContext</c> — so the singleton requirement did
/// not disappear, it relocated to <see cref="DedupingNotificationChannel"/>, which is what actually holds the
/// open-incident set. Asserting "the seam is a singleton" would now be asserting the opposite of the design.
/// </para>
/// <para>
/// Asserted against the <b>real</b> registration rather than a hand-built provider: the invariant is what the
/// composition root does, so a test that wired its own chain would pass while production drifted.
/// </para>
/// </remarks>
public class NotificationRegistrationTests
{
    [Fact]
    public void AddTradingCopilotNotifications_ShouldBindTheSeamToTheOutbox_SoTheFlattenPathNeverAwaitsATransport()
    {
        using WebApplication app = Compose();
        using IServiceScope scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<INotificationChannel>().Should().BeOfType<OutboxNotificationChannel>(
            "the auto-flatten awaits whatever is bound here on the R-13 hot path, and the outbox records to the "
            + "local database rather than waiting on a network — binding a transport directly re-creates gh#289 "
            + "with every test still green");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterTheRelay_SoARecordedPageIsNotSilentlyDropped()
    {
        // Record-and-return is only SAFE because something delivers. Drop the relay and SendAsync still reports
        // success on every page while none is ever sent — a silent failure on the one alert that wakes the
        // operator, and invisible from the caller's side. This is the same guard the pump used to carry.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotNotifications();

        // Reduced to a bool before asserting: `Should().Contain(predicate)` on a service collection renders the
        // entire container in its failure message, which buries the one fact that matters.
        bool relayRegistered = builder.Services.Any(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(NotificationRelayHost));

        relayRegistered.Should().BeTrue("an outbox nobody drains records every page and delivers none");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldBindDedupAsASingleton_SoItRemembersTheOpenIncident()
    {
        // The dedup decorator holds the open-incident set in memory. A scoped registration would forget it between
        // passes, turning "one page per incident" into one page per poll — the alert fatigue ADR-0019 exists to
        // prevent, and the fastest way to train an operator to ignore the pager.
        //
        // gh#400 moved this requirement OFF the seam and onto dedup: the seam is now scoped by necessity, so this
        // is where the singleton property has to be pinned.
        using WebApplication app = Compose();

        using IServiceScope first = app.Services.CreateScope();
        using IServiceScope second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<DedupingNotificationChannel>().Should().BeSameAs(
            second.ServiceProvider.GetRequiredService<DedupingNotificationChannel>(),
            "dedup state lives in that channel, so two relay passes must reach the same instance");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldBindTheSeamAsScoped_BecauseTheOutboxWritesThroughAScopedDbContext()
    {
        // The counterpart to the guard above, and the reason it had to move. The outbox commits intent through the
        // caller's DbContext — that is what lets a safety-critical producer enlist it in its OWN transaction. A
        // singleton seam could not do that, and would also be a captive-dependency bug waiting to happen.
        using WebApplication app = Compose();

        using IServiceScope first = app.Services.CreateScope();
        using IServiceScope second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<INotificationChannel>().Should().NotBeSameAs(
            second.ServiceProvider.GetRequiredService<INotificationChannel>(),
            "each scope must get its own outbox, bound to that scope's DbContext");
    }

    /// <summary>Builds a host with the real notification registration and nothing else.</summary>
    private static WebApplication Compose()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotNotifications();

        // The outbox is scoped and takes the scoped DbContext, so the host needs one to resolve it. In-memory:
        // this suite asserts the SHAPE of the composition, not any database behaviour.
        builder.Services.AddDbContext<MarqSpec.TradingCopilot.Data.TradingCopilotDbContext>(
            options => options.UseInMemoryDatabase("notification-registration-shape"));
        builder.Services.AddSingleton<MarqSpec.TradingCopilot.Data.Tenancy.ICurrentUser>(new NoUser());
        builder.Services.AddSingleton(TimeProvider.System);

        return builder.Build();
    }

    private sealed record NoUser : MarqSpec.TradingCopilot.Data.Tenancy.ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
