namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The operator's own contract caps — an overall ceiling per order plus optional per-instrument limits, so a
/// thinner or faster market can be held tighter than the rest (R-5).
/// </summary>
public sealed record ManualCaps
{
    private ManualCaps(int maxContractsPerOrder, IReadOnlyDictionary<InstrumentId, int> perInstrument)
    {
        MaxContractsPerOrder = maxContractsPerOrder;
        PerInstrument = perInstrument;
    }

    /// <summary>The overall ceiling on contracts in a single order.</summary>
    public int MaxContractsPerOrder { get; }

    /// <summary>Per-instrument ceilings, where the operator has set them.</summary>
    public IReadOnlyDictionary<InstrumentId, int> PerInstrument { get; }

    /// <summary>Creates a set of manual caps.</summary>
    /// <param name="maxContractsPerOrder">The overall per-order ceiling; must not be negative.</param>
    /// <param name="perInstrument">Optional per-instrument ceilings.</param>
    /// <returns>The caps.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxContractsPerOrder"/> is negative.</exception>
    public static ManualCaps Create(
        int maxContractsPerOrder,
        IReadOnlyDictionary<InstrumentId, int>? perInstrument = null)
    {
        if (maxContractsPerOrder < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxContractsPerOrder), maxContractsPerOrder, "A contract cap must not be negative.");
        }

        return new ManualCaps(
            maxContractsPerOrder,
            perInstrument ?? new Dictionary<InstrumentId, int>());
    }

    /// <summary>The tightest manual cap that applies to <paramref name="instrument"/>.</summary>
    /// <param name="instrument">The instrument being traded.</param>
    /// <returns>The per-instrument cap where one is set, otherwise the overall cap — whichever is smaller.</returns>
    public int MaxContractsFor(InstrumentId instrument)
    {
        return PerInstrument.TryGetValue(instrument, out int cap)
            ? Math.Min(cap, MaxContractsPerOrder)
            : MaxContractsPerOrder;
    }
}
