using System.Diagnostics.CodeAnalysis;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The configuration-backed <see cref="IInstrumentSpecSource"/> (gh#541): resolves an instrument's tick size, point
/// value and catastrophic safety-stop distance from <see cref="InstrumentSpecOptions"/>.
/// </summary>
/// <remarks>
/// The merged map is built <b>once</b> in the constructor: the options are static configuration, and a resolve sits
/// on the take path where per-call dictionary rebuilding would be waste. A miss returns
/// <see langword="false"/> and no spec — the fail-closed posture the whole seam exists for.
/// </remarks>
public sealed class InstrumentSpecSource : IInstrumentSpecSource
{
    private readonly IReadOnlyDictionary<string, InstrumentSpecOption> _specs;

    /// <summary>Creates the source over the configured specs.</summary>
    /// <param name="options">The instrument-spec configuration (built-in defaults plus any override).</param>
    public InstrumentSpecSource(IOptions<InstrumentSpecOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _specs = options.Value.ToSpecs();
    }

    /// <inheritdoc />
    public bool TryResolve(InstrumentId instrument, [NotNullWhen(true)] out InstrumentContractSpec? spec)
    {
        if (!_specs.TryGetValue(instrument.ToString(), out InstrumentSpecOption? configured))
        {
            // An explicit miss. Never substitute a default tick size: a fabricated one silently mis-sizes every
            // downstream risk calculation instead of refusing the trade.
            spec = null;
            return false;
        }

        spec = new InstrumentContractSpec(
            InstrumentSpec.Create(instrument, configured.TickSize, configured.PointValue),
            configured.SafetyStopTicks);
        return true;
    }
}
