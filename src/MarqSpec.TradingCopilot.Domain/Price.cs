using System.Globalization;

namespace MarqSpec.TradingCopilot.Domain;

/// <summary>
/// A price expressed as an exact <see cref="decimal"/> — never binary floating point — with tick-size-aware
/// helpers. Prices, sizes, and P&amp;L in the domain are decimal so rounding can never drift a real order.
/// </summary>
/// <param name="Value">The price as an exact decimal.</param>
public readonly record struct Price(decimal Value)
{
    /// <summary>Indicates whether <see cref="Value"/> lies exactly on a multiple of <paramref name="tickSize"/>.</summary>
    /// <param name="tickSize">The instrument's tick size; must be positive.</param>
    /// <returns><see langword="true"/> if the price is on the tick grid.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickSize"/> is not positive.</exception>
    public bool IsOnTick(decimal tickSize)
    {
        EnsurePositive(tickSize);
        return Value % tickSize == 0m;
    }

    /// <summary>Rounds <see cref="Value"/> to the nearest multiple of <paramref name="tickSize"/>.</summary>
    /// <param name="tickSize">The instrument's tick size; must be positive.</param>
    /// <returns>A new <see cref="Price"/> snapped to the tick grid.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickSize"/> is not positive.</exception>
    public Price RoundToTick(decimal tickSize)
    {
        EnsurePositive(tickSize);
        return new Price(Math.Round(Value / tickSize, MidpointRounding.AwayFromZero) * tickSize);
    }

    private static void EnsurePositive(decimal tickSize)
    {
        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "Tick size must be positive.");
        }
    }

    /// <summary>Returns the price value formatted with the invariant culture.</summary>
    /// <returns>The decimal value as an invariant-culture string.</returns>
    public override string ToString()
    {
        return Value.ToString(CultureInfo.InvariantCulture);
    }
}
