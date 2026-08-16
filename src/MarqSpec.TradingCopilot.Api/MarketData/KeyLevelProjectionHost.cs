using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Runs the key-level projection over the clean-historical bar store (gh#597, R-10 / R-22, ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Always runs</b>, like the indicator projection: its work list is whatever the bar store holds, so it needs
/// no configuration of its own and does nothing on an empty store. Follows the established host shape
/// (gh#168/gh#169): a fresh scope per pass, clean exits on cancellation and on root-provider teardown, an injected
/// delay seam. Its cadence is deliberately slower than the indicator pass — key levels form over many bars and sit
/// on no safety-critical path.
/// </para>
/// <para>
/// <b>Attribution.</b> The levels this persists come from <see cref="Domain.MarketData.KeyLevels"/>, whose method
/// and default parameters follow the <i>Bjorgum Key Levels</i> TradingView indicator (© Bjorgum, MPL-2.0, gh#595)
/// — credited, not vendored. The credit rides here too, with the code that stores the result.
/// </para>
/// </remarks>
public sealed class KeyLevelProjectionHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly KeyLevelDetectorOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<KeyLevelProjectionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The key-level cadence and cap.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public KeyLevelProjectionHost(
        IServiceProvider services,
        IOptions<KeyLevelDetectorOptions> options,
        ILogger<KeyLevelProjectionHost> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation("Key-level projection started; over the bar store, every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    KeyLevelProjectionService service =
                        scope.ServiceProvider.GetRequiredService<KeyLevelProjectionService>();

                    int written = await service.ProjectAsync(stoppingToken);
                    if (written > 0)
                    {
                        _logger.LogDebug("Key-level projection wrote or updated {Count} level(s).", written);
                    }
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
                // The service guards per series, so reaching here is a pass-level fault. Levels are recomputable, so
                // a missed pass costs nothing the next one cannot restore.
                _logger.LogError(error, "Key-level projection pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
