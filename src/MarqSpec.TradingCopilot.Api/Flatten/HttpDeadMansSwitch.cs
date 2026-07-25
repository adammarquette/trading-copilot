using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// Pings an external cron-monitor over HTTP (gh#244, ADR-0019) — the outbound half of the dead-man's switch.
/// </summary>
/// <remarks>
/// <para>
/// The monitor must live on <b>infrastructure independent of this app</b>. A monitor sharing this host or this
/// Railway project is not a dead-man's switch — it is a second thing that dies at the same moment, which is the
/// failure the whole mechanism exists to cover.
/// </para>
/// <para>
/// Deliberately failure-tolerant: any transport fault returns <see langword="false"/> rather than throwing.
/// A monitor being unreachable must never fail, delay, or otherwise touch a trading action. Returning false is
/// also honest — a report that did not arrive is exactly what the monitor should page on, so the safe direction
/// and the truthful one coincide. A genuine caller cancellation still propagates, so host shutdown stays clean.
/// </para>
/// </remarks>
public sealed class HttpDeadMansSwitch : IDeadMansSwitch
{
    private readonly HttpClient _client;
    private readonly CheckInOptions _options;
    private readonly ILogger<HttpDeadMansSwitch> _logger;

    /// <summary>Creates the switch over a configured <see cref="HttpClient"/>.</summary>
    /// <param name="client">The client; its timeout bounds how long a hung monitor can hold the loop.</param>
    /// <param name="options">The monitor's check URLs and cadences.</param>
    /// <param name="logger">The logger. Ping URLs are capability URLs and are never written to it.</param>
    public HttpDeadMansSwitch(HttpClient client, IOptions<CheckInOptions> options, ILogger<HttpDeadMansSwitch> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ReportFlatAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        return PingAsync(_options.UrlFor(instrument), instrument.ToString(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ReportAliveAsync(CancellationToken cancellationToken)
    {
        return PingAsync(_options.HeartbeatUri, "liveness", cancellationToken);
    }

    private async Task<bool> PingAsync(Uri? url, string what, CancellationToken cancellationToken)
    {
        if (url is null)
        {
            // Not configured for this check. Not an error -- the operator may monitor only what they trade -- and
            // not logged per call, or an unconfigured deployment would log once a minute forever.
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _client.PostAsync(url, content: null, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Dead-man's switch: reported {Check} to the monitor.", what);
                return true;
            }

            _logger.LogWarning(
                "Dead-man's switch: the monitor rejected the {Check} report with {Status}. It will page if the "
                + "report stays absent.", what, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown -- let the loop exit rather than swallowing it as a timeout
        }
        catch (OperationCanceledException)
        {
            // The client's own timeout. Surfaces as a cancellation, but the caller did not cancel.
            _logger.LogWarning("Dead-man's switch: the {Check} report timed out.", what);
            return false;
        }
        catch (HttpRequestException error)
        {
            // Never rethrow into a safety path. The URL stays out of the message: it is a capability URL, so a
            // log line carrying it would let anyone reading logs forge an all-clear.
            _logger.LogWarning(error, "Dead-man's switch: the {Check} report could not be delivered.", what);
            return false;
        }
    }
}
