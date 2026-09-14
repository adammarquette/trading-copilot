using System.Globalization;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// Reports the effective auto-flatten schedule at startup (gh#255, R-13) — one record per governed market,
/// naming the deadline <b>and where it came from</b>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the safety path was previously silent about its own configuration. The hosts announced
/// their poll cadence and nothing else, so a deployment running a dropped override looked exactly like one
/// running the intended deadline. That is how <c>gh#236</c> — <c>Flatten__</c> settings silently discarded by
/// compose — survived undetected: the app used its defaults and said nothing.
/// </para>
/// <para>
/// Startup-only by design. The schedule is fixed configuration, so re-reporting it on a 15-second loop would
/// bury the one line that matters under noise (engineering §9: degrade deterministically, never fail silently).
/// </para>
/// </remarks>
public sealed class FlattenScheduleReporter
{
    private readonly FlattenOptions _options;
    private readonly ILogger<FlattenScheduleReporter> _logger;

    /// <summary>Creates the reporter over the bound flatten configuration.</summary>
    /// <param name="options">The flatten configuration.</param>
    /// <param name="logger">The logger the report is written to.</param>
    public FlattenScheduleReporter(IOptions<FlattenOptions> options, ILogger<FlattenScheduleReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Writes the effective schedule to the log, one record per governed market.</summary>
    /// <param name="now">The instant whose market date the deadlines are resolved against.</param>
    public void Report(DateTimeOffset now)
    {
        foreach (FlattenScheduleReport report in _options.Describe(now))
        {
            string deadline = report.Deadline.ToString("HH:mm", CultureInfo.InvariantCulture);

            if (!report.Enabled)
            {
                // Above Information deliberately: a market that will NOT be flattened is the one line in this
                // report an operator cannot afford to scroll past. Disabling is the deliberate, warned override
                // taken at their own risk (R-13) -- on a prop account the venue's forced flatten may still
                // backstop it, on a live brokerage account nothing does.
                _logger.LogWarning(
                    "Auto-flatten is DISABLED for {Instrument} ({Source}) — positions will NOT be closed at "
                    + "{Deadline} CT. Deliberate override, at your own risk (R-13).",
                    report.Instrument,
                    report.Source,
                    deadline);
                continue;
            }

            _logger.LogInformation(
                "Auto-flatten armed for {Instrument} at {Deadline} CT ({DeadlineUtc:u}) — {Source}.",
                report.Instrument,
                deadline,
                report.DeadlineUtc,
                report.Source);
        }
    }
}
