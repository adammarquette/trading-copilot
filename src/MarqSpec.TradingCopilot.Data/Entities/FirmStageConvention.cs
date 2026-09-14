using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One operator declaration of what an <see cref="AccountStage"/> means at a <see cref="Firm"/> — whether capital
/// is at risk there (gh#60). The declarations for a firm compose its domain <see cref="FirmConventions"/>.
/// </summary>
/// <remarks>
/// Operator-owned (<see cref="IUserOwned"/>) in its own right, not merely reachable through the firm: a direct
/// query over the declarations must be default-deny scoped too, or it would read every operator's (R-20).
/// </remarks>
public class FirmStageConvention : IUserOwned
{
    /// <summary>The declaration's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The firm this declaration belongs to.</summary>
    public Guid FirmId { get; set; }

    /// <summary>The stage being classified. Never <see cref="AccountStage.Unknown"/> (a DB constraint refuses it).</summary>
    public AccountStage Stage { get; set; }

    /// <summary>Whether capital is at risk at this stage — <see langword="true"/> maps to <see cref="TradingMode.Live"/>.</summary>
    public bool CapitalAtRisk { get; set; }
}
