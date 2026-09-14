namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>One configured instrument's contract facts (gh#541) — overrides a built-in default or adds a new product.</summary>
public sealed class InstrumentSpecOption
{
    /// <summary>The venue-neutral symbol (e.g. <c>ES</c>), matched case-insensitively.</summary>
    public string Symbol { get; init; } = string.Empty;

    /// <summary>The smallest price increment (ES: <c>0.25</c>). Positive.</summary>
    public decimal TickSize { get; init; }

    /// <summary>The dollar value of a one-point move in one contract (ES: <c>50</c>). Positive.</summary>
    public decimal PointValue { get; init; }

    /// <summary>How many ticks beyond the entry the catastrophic safety stop sits. Positive.</summary>
    public int SafetyStopTicks { get; init; }
}

/// <summary>
/// The per-instrument contract specs a <b>server-originated</b> proposal needs to become an order ticket (gh#541,
/// R-4 / R-16, ADR-0007) — bound from the <c>InstrumentSpecs</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="Flatten.FlattenOptions"/>: built-in defaults for the products the flatten schedule
/// already governs, which configuration may <b>override</b> or <b>extend</b>. That keeps a fresh deployment working
/// out of the box while leaving every number operator-settable.
/// </para>
/// <para>
/// <b>There is deliberately no global default.</b> An unconfigured instrument is an explicit miss, never a
/// substituted tick size — a fabricated one does not fail loudly, it silently mis-sizes every downstream risk
/// calculation. Validated on start, so a nonsensical spec fails the host once rather than at take time.
/// </para>
/// </remarks>
public sealed class InstrumentSpecOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "InstrumentSpecs";

    /// <summary>
    /// The built-in specs, for the same four products <see cref="Flatten.FlattenOptions"/> ships deadlines for.
    /// Standard CME contract facts; configuration overrides any of them.
    /// </summary>
    internal static IReadOnlyDictionary<string, InstrumentSpecOption> Defaults { get; } =
        new Dictionary<string, InstrumentSpecOption>(StringComparer.OrdinalIgnoreCase)
        {
            // E-mini S&P 500: 0.25 tick, $50/point => $12.50 a tick.
            ["ES"] = new() { Symbol = "ES", TickSize = 0.25m, PointValue = 50m, SafetyStopTicks = 40 },

            // E-mini Nasdaq-100: 0.25 tick, $20/point => $5.00 a tick.
            ["NQ"] = new() { Symbol = "NQ", TickSize = 0.25m, PointValue = 20m, SafetyStopTicks = 60 },

            // Crude oil: 0.01 tick, $1,000/point => $10.00 a tick.
            ["CL"] = new() { Symbol = "CL", TickSize = 0.01m, PointValue = 1000m, SafetyStopTicks = 50 },

            // Gold: 0.10 tick, $100/point => $10.00 a tick.
            ["GC"] = new() { Symbol = "GC", TickSize = 0.10m, PointValue = 100m, SafetyStopTicks = 50 },
        };

    /// <summary>Per-instrument overrides of the built-in defaults; may also add an instrument not among them.</summary>
    public InstrumentSpecOption[] Instruments { get; init; } = [];

    /// <summary>
    /// Validates every configured entry. Run on start so a bad spec cannot boot the host — the values feed the sizing
    /// ladder and the fat-finger band, where a silent zero would be far worse than a refused startup.
    /// </summary>
    /// <returns><see langword="true"/> when every configured entry is well-formed.</returns>
    public bool Validate() =>
        Instruments.All(instrument =>
            !string.IsNullOrWhiteSpace(instrument.Symbol)
            && instrument.TickSize > 0m
            && instrument.PointValue > 0m
            && instrument.SafetyStopTicks > 0);

    /// <summary>Resolves the effective specs: the built-in defaults with any configured entry applied on top.</summary>
    /// <returns>One entry per known instrument, keyed case-insensitively by symbol.</returns>
    public IReadOnlyDictionary<string, InstrumentSpecOption> ToSpecs()
    {
        Dictionary<string, InstrumentSpecOption> merged = new(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (InstrumentSpecOption instrument in Instruments)
        {
            if (!string.IsNullOrWhiteSpace(instrument.Symbol))
            {
                // A configured entry replaces the default wholesale rather than merging field-by-field: a partially
                // overridden spec (a new tick size against an old point value) is a silently wrong contract.
                merged[instrument.Symbol.Trim()] = instrument;
            }
        }

        return merged;
    }
}
