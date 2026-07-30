using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Drives the news-embedding pass (gh#377, R-2) — the periodic host that populates the <c>pgvector</c> embedding
/// behind each ingested news item. Mirrors <c>NewsRelevanceHost</c>: always on, a fresh DI scope per pass, and
/// injected clock/delay seams so the cadence is deterministic under test.
/// </summary>
/// <remarks>
/// Always on — its work list is whatever news needs embedding (never embedded, or revised since), so there is
/// nothing to opt into; with no news, or no embedding provider configured, the pass is a cheap no-op. A per-pass
/// fault is logged and the loop continues; a missed pass self-heals because the un-embedded rows are still there
/// next pass.
/// </remarks>
public sealed class NewsEmbeddingHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly NewsEmbeddingOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<NewsEmbeddingHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The pass cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public NewsEmbeddingHost(
        IServiceProvider services,
        IOptions<NewsEmbeddingOptions> options,
        ILogger<NewsEmbeddingHost> logger,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation("News embedding started, running every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    NewsEmbeddingService service = scope.ServiceProvider.GetRequiredService<NewsEmbeddingService>();
                    await service.EmbedPendingAsync(_now(), stoppingToken);
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
                _logger.LogError(error, "News embedding pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
