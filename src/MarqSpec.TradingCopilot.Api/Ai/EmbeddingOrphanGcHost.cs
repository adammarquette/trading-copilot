using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Periodically deletes orphaned <c>EmbeddingRecord</c> rows — those whose owner was renamed or pruned (gh#902) — the
/// half of the embedding model-migration hygiene the stale-model sweep (gh#889) cannot reach, since a re-embed never
/// touches a dead owner. Mirrors <see cref="NewsEmbeddingHost"/>: a fresh DI scope per pass and an injected delay seam
/// so the cadence is deterministic under test. Runs on a long, <b>conservative</b> cadence off the hot embed path,
/// because the delete is irreversible (a paid re-embed if the owner returns).
/// </summary>
/// <remarks>
/// The per-pass work is the static <see cref="SweepAsync"/> — taking the store seam so it is unit-testable — while
/// this loop is thin plumbing (scope, invoke, delay, log-and-continue on fault), the same split
/// <see cref="Suggestions.SuggestionExpiryHost"/> uses.
/// </remarks>
public sealed class EmbeddingOrphanGcHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly EmbeddingOrphanGcOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<EmbeddingOrphanGcHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per sweep.</param>
    /// <param name="options">The sweep cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public EmbeddingOrphanGcHost(
        IServiceProvider services,
        IOptions<EmbeddingOrphanGcOptions> options,
        ILogger<EmbeddingOrphanGcHost> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Runs one GC pass, best-effort: the orphaned-owner sweep over every <see cref="EmbeddingOrphanSweep.SweepableKinds"/>
    /// (gh#902), then — when a current model is known — the crash-leaked stale-model backstop (gh#915). A fault in any
    /// one step is logged and never stops the rest; only a genuine caller cancellation propagates.
    /// </summary>
    /// <param name="store">The orphan store seam.</param>
    /// <param name="currentModel">
    /// The embedding provider's current model, or <see langword="null"/> when no provider is configured — the
    /// stale-model backstop is skipped in that case, since with no current model there is no duplicate to resolve.
    /// </param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The total number of rows deleted this pass.</returns>
    public static async Task<int> SweepAsync(
        IEmbeddingOrphanStore store, string? currentModel, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        int total = 0;
        foreach (EmbeddingOwnerKind ownerKind in EmbeddingOrphanSweep.SweepableKinds)
        {
            try
            {
                total += await store.DeleteOrphansAsync(ownerKind, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // host shutdown, not a per-kind fault to swallow
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Embedding orphan sweep for {OwnerKind} failed; other kinds continue.", ownerKind);
            }
        }

        // gh#915 crash-leaked stale-model backstop -- runs only when a current model is known (a provider is
        // configured); with none there is no current-model sibling to prove a duplicate, so there is nothing to sweep.
        if (currentModel is not null)
        {
            try
            {
                total += await store.DeleteStaleModelDuplicatesAsync(currentModel, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // host shutdown, not a fault to swallow
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Embedding stale-model backstop sweep failed; the harmless stale rows remain.");
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Embedding orphan GC swept {Count} row(s).", total);
        }

        return total;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = _options.SweepInterval;
        _logger.LogInformation("Embedding orphan GC started, sweeping every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IEmbeddingOrphanStore store = scope.ServiceProvider.GetRequiredService<IEmbeddingOrphanStore>();
                    // The backstop needs the model every read pins to; with no provider configured there is no
                    // current model, so it is skipped (null) and only the orphaned-owner sweep runs.
                    IEmbeddingProvider provider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
                    string? currentModel = provider.IsAvailable ? provider.Model : null;
                    await SweepAsync(store, currentModel, _logger, stoppingToken);
                }

                await _delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Embedding orphan GC sweep failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
