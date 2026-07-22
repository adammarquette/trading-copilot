using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>Bridges a persisted <see cref="Firm"/> to the domain <see cref="FirmConventions"/> value object.</summary>
public static class FirmConventionsMapping
{
    /// <summary>Builds the firm's declared conventions from its persisted stage declarations.</summary>
    /// <param name="firm">The firm, with <see cref="Firm.StageConventions"/> loaded.</param>
    /// <returns>
    /// The conventions. With no declarations every stage resolves to <see cref="TradingMode.Undeclared"/>, the
    /// same fail-closed answer as <see cref="FirmConventions.None"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A declaration is a duplicate or an undeclarable stage — which the DB constraints refuse to persist, so in
    /// practice this signals corrupted data rather than a caller error.
    /// </exception>
    public static FirmConventions ToConventions(this Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);

        return FirmConventions.For(
            firm.Name,
            [.. firm.StageConventions.Select(convention => (convention.Stage, convention.CapitalAtRisk))]);
    }
}
