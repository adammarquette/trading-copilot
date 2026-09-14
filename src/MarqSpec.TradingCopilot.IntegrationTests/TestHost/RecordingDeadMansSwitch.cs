using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A recording stand-in for the <b>external monitor</b> the dead-man's switch reports to (gh#408, gh#244). Like the
/// venue, this is an <b>outbound seam to a third party</b> that cannot exist pre-merge — the whole point of the
/// switch is that something <i>outside</i> this process is watching. It is adversarial in the sense the QA contract
/// requires: it <b>never supplies the decision</b>. It only records what the service chose to say, and can be told
/// to <b>reject</b> a report so the retry property is observable.
/// </summary>
public sealed class RecordingDeadMansSwitch : IDeadMansSwitch
{
    private readonly List<InstrumentId> _flatReports = [];
    private readonly Dictionary<InstrumentId, int> _rejectionsRemaining = [];
    private readonly Lock _gate = new();

    /// <summary>Every instrument this process vouched as flat, in call order.</summary>
    public IReadOnlyList<InstrumentId> FlatReports
    {
        get
        {
            lock (_gate)
            {
                return [.. _flatReports];
            }
        }
    }

    /// <summary>How many liveness heartbeats were sent — distinct from a flat report, never a substitute for one.</summary>
    public int AliveReports { get; private set; }

    /// <summary>
    /// Makes the next <paramref name="count"/> flat reports <b>for one instrument</b> fail — the monitor refused to
    /// accept them. Instrument-scoped on purpose: several markets can be due in the same pass, so a positional
    /// "reject the next report" would land on whichever happened to be reported first.
    /// </summary>
    public void RejectNextFor(InstrumentId instrument, int count = 1)
    {
        lock (_gate)
        {
            _rejectionsRemaining[instrument] = count;
        }
    }

    /// <summary>How many times <paramref name="instrument"/> was reported — attempts, accepted or refused.</summary>
    public int ReportCountFor(InstrumentId instrument)
    {
        lock (_gate)
        {
            return _flatReports.Count(reported => reported == instrument);
        }
    }

    /// <summary>Clears recorded reports and any pending rejections — call at the start of each test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _flatReports.Clear();
            _rejectionsRemaining.Clear();
            AliveReports = 0;
        }
    }

    /// <inheritdoc />
    public Task<bool> ReportFlatAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // The attempt is recorded either way -- a rejected report was still SENT, and a test proving the retry
            // needs to tell "never attempted" apart from "attempted and refused".
            _flatReports.Add(instrument);
            if (_rejectionsRemaining.TryGetValue(instrument, out int remaining) && remaining > 0)
            {
                _rejectionsRemaining[instrument] = remaining - 1;
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> ReportAliveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            AliveReports++;
            return Task.FromResult(true);
        }
    }
}
