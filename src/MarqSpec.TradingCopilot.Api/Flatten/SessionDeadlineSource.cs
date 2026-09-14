using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The <see cref="ISessionDeadlineSource"/> over the configured flatten schedules (gh#544) — one source of truth for
/// a market's deadline, so a consumer that merely needs to <b>respect</b> it never depends on the machinery that
/// <b>acts</b> on it.
/// </summary>
/// <remarks>
/// It lives beside the flatten options it reads, and hands out only a <see cref="TimeOnly"/>. A <b>disabled</b>
/// market reports <see langword="null"/> rather than its configured time: disabling auto-flatten is a deliberate,
/// warned override (R-13), and once it is off there is no deadline a suggestion needs to expire before.
/// </remarks>
public sealed class SessionDeadlineSource : ISessionDeadlineSource
{
    private readonly IReadOnlyDictionary<string, TimeOnly> _deadlines;

    /// <summary>Creates the source over the configured schedules.</summary>
    /// <param name="options">The flatten configuration the schedules are resolved from.</param>
    public SessionDeadlineSource(IOptions<FlattenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolved once: the schedules are static configuration, and this is read on every agent-review fire.
        _deadlines = options.Value.ToSchedules()
            .Where(schedule => schedule.Enabled)
            .ToDictionary(
                schedule => schedule.Instrument.ToString(),
                schedule => schedule.Deadline,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public TimeOnly? DeadlineFor(InstrumentId instrument) =>
        _deadlines.TryGetValue(instrument.ToString(), out TimeOnly deadline) ? deadline : null;
}
