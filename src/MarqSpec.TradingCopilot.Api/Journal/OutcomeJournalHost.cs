using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>
/// Runs the <see cref="OutcomeJournalService"/> over the journal on a cadence (gh#909, R-9): each pass composes an
/// <c>Outcome</c> for closed trades that lack one.
/// </summary>
/// <remarks>
/// <b>Always runs</b>, like <c>IndicatorProjectionHost</c> — its work list is whatever the journal holds, so an empty
/// journal costs nothing. Follows the established host shape (gh#168 / gh#169): a fresh scope per pass, clean exits on
/// cancellation and on root-provider teardown, and an injected <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// seam so a test drives the cadence.
/// </remarks>
public sealed class OutcomeJournalHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly OutcomeJournalOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<OutcomeJournalHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The writer cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public OutcomeJournalHost(
        IServiceProvider services,
        IOptions<OutcomeJournalOptions> options,
        ILogger<OutcomeJournalHost> logger,
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
        _logger.LogInformation(
            "Outcome writer started; composing outcomes for closed trades every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
                    await service.ComposeClosedTradeOutcomesAsync(stoppingToken);
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
                // The service guards per trade and outcomes are recomposable, so a missed pass costs nothing the next
                // one cannot restore.
                _logger.LogError(error, "Outcome writer pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
