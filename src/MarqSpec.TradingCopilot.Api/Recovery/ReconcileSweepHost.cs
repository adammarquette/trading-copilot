using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The runtime reconcile-strand <b>detection</b> sweep (gh#722, ADR-0013): a periodic background pass that finds an
/// order stranded <c>Taking</c> or a conditional stranded <c>Firing</c> past an age bound and <b>raises an operator
/// alert</b> so the operator reconciles it through the existing <c>POST /orders/{id}/reconcile</c> /
/// <c>POST /conditionals/{id}/reconcile</c>. It is the <b>runtime</b> sibling of the restart-time
/// <see cref="DecisionStateRehydrator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Propose-and-confirm — it never acts.</b> The sweep transmits nothing, adopts nothing, releases nothing and
/// changes no order state; it only detects and alerts, preserving the "never auto-act on rehydrated state" invariant
/// (ADR-0013). A strand becomes safe only when the operator reconciles it against venue truth.
/// </para>
/// <para>
/// A <b>fresh DI scope per pass</b> keeps host shutdown clean (the suggestion-expire / auto-flatten discipline): the
/// DbContext is scoped, so holding one for the app's lifetime would leak tracked entities and, on teardown, leave
/// this loop acting on disposed services. The clock is read <b>here</b>, at the boundary, and passed into the pure
/// <see cref="ReconcileStrandDetection.Detect"/>, so the decision stays side-effect-free. Age is measured by the
/// in-memory <see cref="ReconcileStrandRegister"/> (first-seen per process lifetime), not a database column: a
/// restart strand is already covered by the rehydrator, so the register starting empty at boot is not a gap.
/// </para>
/// <para>
/// The DB discovery and the alert wiring are exercised by the QA integration tier (the relational-only boundary,
/// like the other sweeps); the pure decision and the alert/meter fan-out (<see cref="DetectAndAlert"/>) are
/// unit-tested here.
/// </para>
/// </remarks>
public sealed class ReconcileSweepHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ReconcileStrandRegister _register;
    private readonly IExecutionMetrics _metrics;
    private readonly ReconcileSweepOptions _options;
    private readonly ILogger<ReconcileSweepHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="register">The process-wide first-seen register (singleton, survives per-pass scopes).</param>
    /// <param name="metrics">The execution-SLI sink the strand-detected counter is recorded through.</param>
    /// <param name="options">The cadence and age bound.</param>
    /// <param name="logger">The logger — the interim operator-alert channel (<c>synthetic_risk</c>), mirroring the rehydrator.</param>
    public ReconcileSweepHost(
        IServiceProvider services,
        ReconcileStrandRegister register,
        IExecutionMetrics metrics,
        IOptions<ReconcileSweepOptions> options,
        ILogger<ReconcileSweepHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _register = register;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // A deliberate, configured opt-out (never a silent omission): say so, loudly enough to be found, and stop.
            _logger.LogWarning("Reconcile strand sweep is disabled by configuration; stranded intents will surface "
                + "only on restart via the rehydrator (gh#722).");
            return;
        }

        TimeSpan interval = _options.SweepInterval;
        _logger.LogInformation(
            "Reconcile strand sweep started; scanning every {Interval}, alerting a strand past {AgeBound} (gh#722).",
            interval, _options.StrandAgeBound);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A FRESH scope per pass: the DbContext is scoped, so holding one for the app's lifetime would leak
                // tracked entities and, on host teardown, leave this loop acting on disposed services.
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    TradingCopilotDbContext database =
                        scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
                    IReadOnlyList<ReconcileStrandKey> stranded = await DiscoverStrandedAsync(database, stoppingToken);
                    int alerted = DetectAndAlert(
                        stranded, _register, _metrics, _logger, DateTimeOffset.UtcNow, _options.StrandAgeBound);
                    if (alerted > 0)
                    {
                        _logger.LogInformation(
                            "Reconcile strand sweep raised {Count} new strand alert(s) awaiting operator reconcile (gh#722).",
                            alerted);
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly
            }
            catch (Exception error)
            {
                // A transient DB fault must not kill the sweep: log, back off, retry. Detection is idempotent, so a
                // failed pass loses nothing -- the register keeps its first-seens and the next pass alerts what is due.
                _logger.LogWarning(error, "Reconcile strand sweep failed; retrying after {Delay}.", interval);
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Discovers every currently-stranded intent across all owners (gh#722): orders left <c>Taking</c> and
    /// conditionals left <c>Firing</c>. Background plumbing with no request user, so the R-20 default-deny filter is
    /// bypassed with <c>IgnoreQueryFilters</c> — the discovery discipline of
    /// <see cref="MarketData.ConditionalFiringService"/> and the rehydrator; ownership is carried on every key.
    /// </summary>
    /// <param name="database">The pass-scoped database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>One key per stranded order and conditional.</returns>
    private static async Task<IReadOnlyList<ReconcileStrandKey>> DiscoverStrandedAsync(
        TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        // Project straight to the owner-carrying key in the query (the RehydratedOrder projection idiom): the kind is
        // a client constant, the owner and id are columns. Ownership is carried on every key, never dropped (R-20).
        List<ReconcileStrandKey> takingOrders = await database.Orders
            .IgnoreQueryFilters()
            .Where(order => order.Status == OrderStatus.Taking)
            .Select(order => new ReconcileStrandKey(ReconcileStrandKind.OrderTaking, order.UserId, order.Id))
            .ToListAsync(cancellationToken);

        List<ReconcileStrandKey> firingConditionals = await database.ConditionalOrders
            .IgnoreQueryFilters()
            .Where(order => order.Status == ConditionalStatus.Firing)
            .Select(order => new ReconcileStrandKey(ReconcileStrandKind.ConditionalFiring, order.UserId, order.Id))
            .ToListAsync(cancellationToken);

        return [.. takingOrders, .. firingConditionals];
    }

    /// <summary>
    /// Runs the pure <see cref="ReconcileStrandDetection.Detect"/> against the register and, for each <b>newly</b>
    /// alertable strand, raises the loud operator alert (<c>synthetic_risk</c>, the rehydrator's interim channel) and
    /// increments the strand-detected metric — then swaps the register to the decision's next state. Idempotent: a
    /// strand that stays stranded across passes alerts and meters exactly once. Internal, not private, so the
    /// alert/meter fan-out can be unit-tested without spinning the background loop or a database.
    /// </summary>
    /// <param name="stranded">The strands this pass discovered.</param>
    /// <param name="register">The first-seen register.</param>
    /// <param name="metrics">The metric sink.</param>
    /// <param name="logger">The alert channel.</param>
    /// <param name="now">The pass instant.</param>
    /// <param name="ageBound">How long a strand must persist before it is alertable.</param>
    /// <returns>How many strands newly alerted this pass.</returns>
    internal static int DetectAndAlert(
        IReadOnlyList<ReconcileStrandKey> stranded,
        ReconcileStrandRegister register,
        IExecutionMetrics metrics,
        ILogger logger,
        DateTimeOffset now,
        TimeSpan ageBound)
    {
        ReconcileStrandDecision decision =
            ReconcileStrandDetection.Detect(stranded, register.Snapshot(), now, ageBound);

        foreach (ReconcileStrandKey strand in decision.NewlyAlertable)
        {
            // Loud, per strand, and never auto-repaired -- the exact treatment the rehydrator gives an OrderMidTaking /
            // ConditionalMidFiring at boot, carrying synthetic_risk until the formal operator channel lands (gh#209).
            logger.LogCritical(
                "Reconcile sweep detected a stranded {Kind} intent (synthetic_risk) past the {AgeBound} age bound: it "
                + "may be live at the venue with no journal behind it [owner {Owner}, entity {EntityId}]. Nothing was "
                + "transmitted, adopted, or released -- resolve it against venue truth via {ReconcileRoute}.",
                strand.Kind, ageBound, strand.Owner, strand.EntityId, ReconcileRoute(strand.Kind));

            metrics.RecordReconcileStrandDetected(KindTag(strand.Kind));
        }

        register.Reconcile(decision.Register);
        return decision.NewlyAlertable.Count;
    }

    private static string KindTag(ReconcileStrandKind kind) => kind switch
    {
        ReconcileStrandKind.OrderTaking => ExecutionMetrics.ReconcileStrandOrderTaking,
        ReconcileStrandKind.ConditionalFiring => ExecutionMetrics.ReconcileStrandConditionalFiring,
        _ => "unknown",
    };

    private static string ReconcileRoute(ReconcileStrandKind kind) => kind switch
    {
        ReconcileStrandKind.OrderTaking => "POST /orders/{id}/reconcile",
        ReconcileStrandKind.ConditionalFiring => "POST /conditionals/{id}/reconcile",
        _ => "the reconcile endpoint",
    };
}
