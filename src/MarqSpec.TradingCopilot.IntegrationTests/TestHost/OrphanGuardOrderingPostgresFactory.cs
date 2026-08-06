using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the orphan-guard commit-then-journal ORDERING guard (gh#715, the gap gh#707/PR #710 surfaced).
/// Decorates the composition root's <see cref="IEventLog"/> registration so that, at the instant
/// <c>OrphanGuardService</c> appends <c>protection.orphaned</c> / <c>protection.restored</c>, an observer reads the
/// transitioning <c>StopPlan</c>'s staging from a <b>fresh DI scope</b> — <i>during</i> the pass, not after it
/// returns. This is the <c>ObservingGuard</c> shape (gh#629 / PR #667,
/// <see cref="MarqSpec.TradingCopilot.UnitTests.Api.MarketData.ConditionalFiringServiceTests"/>): a seam that reads
/// committed state at the moment a concurrent consumer could also read it, so the assertion witnesses the
/// intermediate window rather than the terminal state both orderings leave identical (the exact reason
/// <c>OrphanGuardEventLogIntegrationTests</c>'s two consistency cases cannot prove ordering — see their remarks).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, not a race.</b> <c>OrphanGuardService.OrphanAsync</c>/<c>RearmAsync</c> commit the
/// <c>StopPlan</c> transition via <c>SaveChangesAsync</c> — a synchronously-awaited, already-committed write on
/// Npgsql — strictly before or after the journal call, never concurrently with it. So a fresh-scope read taken
/// AT the append call deterministically observes whichever write happened first: the pre-transition staging if the
/// journal ran first (the defect this guards against), the post-transition staging if the commit ran first (the
/// shipped, correct order).
/// </para>
/// <para>
/// <b>The gh#382 trap.</b> The composition root binds <c>IEventLog</c> directly to <see cref="TimescaleEventLog"/>
/// (<c>Program.cs</c>) — no decorator sits there today, so this factory is the first to add one. Re-registering
/// <c>IEventLog</c> outright would still be the same trap in miniature: a hand-rolled double would observe (and
/// prove ordering against) a system nobody deploys. Instead the displaced registration's own implementation is kept
/// alive under its concrete type and wrapped, so the append this suite observes is the genuine
/// <see cref="TimescaleEventLog"/> writing to real Postgres — <see cref="DisplacedRegistration"/> is kept so
/// <c>OrphanGuardEventOrderingIntegrationTests</c> can assert what was actually displaced, the same fidelity check
/// <c>InstrumentationSafetyPostgresFactory</c> established.
/// </para>
/// </remarks>
/// <remarks>
/// Subclasses <see cref="StubbedVenuePostgresFactory"/> directly (not <c>OrphanTestPostgresFactory</c>, which is
/// <see langword="sealed"/>) and re-derives its host-stripping: the only orphan / re-arm pass must be this suite's
/// own explicit call, or the always-on <c>VenueConnectionMonitorHost</c> would transition the seeded
/// <c>StopPlan</c> out from under the very instant this factory means to observe.
/// </remarks>
public sealed class OrphanGuardOrderingPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>
    /// The <see cref="IEventLog"/> registration this factory displaced — i.e. what the composition root binds in
    /// production. Kept so a test can assert this harness really wraps <see cref="TimescaleEventLog"/> rather than
    /// reproducing something the deployment does not have (gh#382).
    /// </summary>
    public ServiceDescriptor? DisplacedRegistration { get; private set; }

    /// <summary>The shared capture point a test arms before driving a pass, and reads afterward.</summary>
    public EventOrderingObserver Observer { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host, same reasoning as OrphanTestPostgresFactory (gh#192): the only orphan / re-arm pass
        // is this suite's own explicit call, or the always-on VenueConnectionMonitorHost would orphan/rearm the
        // seeded StopPlan out from under the very instant this suite means to observe.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        ServiceDescriptor? existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IEventLog));
        if (existing is not null)
        {
            DisplacedRegistration = existing;
            services.Remove(existing);
        }

        // The displaced implementation stays reachable under its OWN concrete type, constructed by DI exactly as
        // production would (it still depends on the scoped TradingCopilotDbContext) — this is the "wrap, don't
        // replace" half of gh#382: IEventLog resolves to the decorator, but the decorator's inner IS the shipped type.
        services.AddScoped<TimescaleEventLog>();
        services.AddScoped<IEventLog>(provider => new ObservingEventLog(
            provider.GetRequiredService<TimescaleEventLog>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Observer));
    }
}

/// <summary>
/// The capture point for the ordering guard: a test sets <see cref="PlanId"/> to the <c>StopPlan</c> it is watching
/// before driving <c>OrphanAsync</c>/<c>RearmAsync</c>, then reads <see cref="StagingAtAppend"/> afterward.
/// </summary>
public sealed class EventOrderingObserver
{
    /// <summary>The <c>StopPlan</c> a running test is watching. Set before the pass; read by the decorator during it.</summary>
    public Guid PlanId { get; set; }

    /// <summary>
    /// The staging <see cref="ObservingEventLog"/> read, from a fresh scope, at the instant the protection event was
    /// appended. <see langword="null"/> until an orphan/restored append actually happens.
    /// </summary>
    public StopStaging? StagingAtAppend { get; private set; }

    /// <summary>Called by the decorator — not test code — at the moment of append.</summary>
    internal void Capture(StopStaging staging) => StagingAtAppend = staging;

    /// <summary>Clears the previous capture. Call at the start of each test for isolation.</summary>
    public void Reset() => StagingAtAppend = null;
}

/// <summary>
/// Wraps a real <see cref="IEventLog"/>. On the two orphan-guard protection event types, it reads the watched
/// <c>StopPlan</c>'s COMMITTED staging from a brand-new DI scope — a fresh connection, no shared change tracker —
/// <b>before</b> delegating to the inner log's own append. Every other event type, and every other member, passes
/// straight through untouched.
/// </summary>
public sealed class ObservingEventLog(IEventLog inner, IServiceScopeFactory scopeFactory, EventOrderingObserver observer) : IEventLog
{
    /// <inheritdoc />
    public async Task<EventEnvelope> AppendAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Type is OrphanGuardService.OrphanedEventType or OrphanGuardService.RestoredEventType)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            StopStaging staging = await database.StopPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(plan => plan.Id == observer.PlanId)
                .Select(plan => plan.Staging)
                .SingleAsync(cancellationToken);
            observer.Capture(staging);
        }

        return await inner.AppendAsync(draft, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EventPage> ReadAfterAsync(long afterSequence, int limit, CancellationToken cancellationToken) =>
        inner.ReadAfterAsync(afterSequence, limit, cancellationToken);

    /// <inheritdoc />
    public Task<long?> GetCursorAsync(string consumerGroup, CancellationToken cancellationToken) =>
        inner.GetCursorAsync(consumerGroup, cancellationToken);

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetCursorCommittedAtAsync(string consumerGroup, CancellationToken cancellationToken) =>
        inner.GetCursorCommittedAtAsync(consumerGroup, cancellationToken);

    /// <inheritdoc />
    public Task CommitCursorAsync(string consumerGroup, long sequence, CancellationToken cancellationToken) =>
        inner.CommitCursorAsync(consumerGroup, sequence, cancellationToken);
}
