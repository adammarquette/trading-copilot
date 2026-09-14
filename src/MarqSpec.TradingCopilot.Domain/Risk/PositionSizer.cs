namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>Turns a money budget into a contract count. Always rounds down — the extra contract is the one that
/// puts the loss past the budget.</summary>
public static class PositionSizer
{
    /// <summary>How many contracts <paramref name="riskBudget"/> covers at <paramref name="lossPerContract"/>.</summary>
    /// <param name="riskBudget">The money available to lose. Zero or negative means no room.</param>
    /// <param name="lossPerContract">What one contract loses at the stop; must be positive.</param>
    /// <returns>The contract count, rounded down, never negative.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lossPerContract"/> is not positive.</exception>
    public static int MaxContracts(decimal riskBudget, decimal lossPerContract)
    {
        if (lossPerContract <= 0m)
        {
            // A zero-distance stop implies unbounded size. Refuse rather than size against it.
            throw new ArgumentOutOfRangeException(
                nameof(lossPerContract), lossPerContract, "Loss per contract must be positive.");
        }

        if (riskBudget <= 0m)
        {
            return 0;
        }

        decimal contracts = Math.Floor(riskBudget / lossPerContract);

        return contracts >= int.MaxValue ? int.MaxValue : (int)contracts;
    }
}
