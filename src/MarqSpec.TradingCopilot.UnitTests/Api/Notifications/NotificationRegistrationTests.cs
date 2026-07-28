using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The <b>shape</b> of the notification composition (gh#320 ⇒ gh#289, R-13, ADR-0019). <see cref="QueuedNotificationChannel"/>
/// already has its own behavioural suite; what nothing guarded is that the composition root actually <i>binds</i> it —
/// and the auto-flatten's protection is entirely a property of that binding, not of anything the flatten service does.
/// </summary>
/// <remarks>
/// <para>
/// <c>AutoFlattenService.NotifyAsync</c> awaits <c>INotificationChannel.SendAsync</c> with the caller's token and has
/// <b>no bound of its own</b> — it catches a channel that <i>throws</i>, not one that <i>hangs</i>. So "a hung channel
/// can never delay a flatten" is true only while the resolved channel is the queue. Bind a blocking transport there
/// (<see cref="PushoverNotificationChannel"/> carries a 10-second <c>HttpClient</c> timeout) and the R-13 hot path
/// silently regains an unbounded await — at the exact moment a position is already failing to close.
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
    public void AddTradingCopilotNotifications_ShouldBindTheChannelToTheQueue_SoTheFlattenPathNeverAwaitsATransport()
    {
        using WebApplication app = Compose();

        app.Services.GetRequiredService<INotificationChannel>().Should().BeOfType<QueuedNotificationChannel>(
            "the auto-flatten awaits whatever is bound here on the R-13 hot path, and only the queue returns without "
            + "waiting on a network — binding a transport directly re-creates gh#289 with every test still green");
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
    public void AddTradingCopilotNotifications_ShouldBindTheChannelAsASingleton_SoDedupRemembersTheOpenIncident()
    {
        // The dedup decorator holds the open-incident set in memory. A scoped registration would forget it between
        // passes, turning "one page per incident" into one page per 15-second poll — the alert fatigue ADR-0019
        // exists to prevent, and the fastest way to train an operator to ignore the pager.
        using WebApplication app = Compose();

        using IServiceScope first = app.Services.CreateScope();
        using IServiceScope second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<INotificationChannel>().Should().BeSameAs(
            second.ServiceProvider.GetRequiredService<INotificationChannel>(),
            "dedup state lives in the channel, so two passes must reach the same instance");
    }

    /// <summary>Builds a host with the real notification registration and nothing else.</summary>
    private static WebApplication Compose()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotNotifications();
        return builder.Build();
    }
}
