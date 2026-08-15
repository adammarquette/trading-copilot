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

        // The one place the persisted taxonomy becomes a domain behaviour (gh#780). A brokerage has no stage
        // ladder to declare — its account names resolve to AccountStage.Unknown, which FirmConventions.For
        // refuses on purpose — so it does not go through the per-stage model at all; its mode comes from the
        // venue's own live/paper flag, which at a brokerage is the honest answer rather than the misleading one
        // R-14 was written about.
        return firm.Type == FirmType.Brokerage
            ? FirmConventions.ForBrokerage(firm.Name)
            : FirmConventions.For(
                firm.Name,
                [.. firm.StageConventions.Select(convention => (convention.Stage, convention.CapitalAtRisk))]);
    }
}
