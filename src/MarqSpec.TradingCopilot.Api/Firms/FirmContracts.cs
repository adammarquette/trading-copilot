using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Firms;

/// <summary>The request to register a firm the operator trades with.</summary>
/// <param name="Name">The firm's name (e.g. <c>Topstep</c>).</param>
/// <param name="Type">Prop firm (staged) or brokerage (live/paper only).</param>
public sealed record CreateFirmRequest(string Name, FirmType Type);

/// <summary>One stage declaration — what a stage means at a firm (gh#60).</summary>
/// <param name="Stage">The stage being classified. Never <see cref="AccountStage.Unknown"/>.</param>
/// <param name="CapitalAtRisk"><see langword="true"/> if capital is at risk at this stage (maps to Live).</param>
public sealed record StageConventionDto(AccountStage Stage, bool CapitalAtRisk);

/// <summary>The request to declare a firm's full set of stage conventions (replaces any existing).</summary>
/// <param name="Conventions">The declarations. Each stage may appear at most once, and none may be Unknown.</param>
public sealed record DeclareConventionsRequest(IReadOnlyList<StageConventionDto> Conventions);

/// <summary>A firm as returned to the operator, with its declared conventions.</summary>
/// <param name="Id">The firm record's id.</param>
/// <param name="Name">The firm's name.</param>
/// <param name="Type">Prop firm or brokerage.</param>
/// <param name="Conventions">The declared stage conventions.</param>
public sealed record FirmResponse(Guid Id, string Name, FirmType Type, IReadOnlyList<StageConventionDto> Conventions)
{
    /// <summary>Projects a persisted firm (with its conventions loaded) to the response shape.</summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The response.</returns>
    public static FirmResponse From(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);

        return new FirmResponse(
            firm.Id,
            firm.Name,
            firm.Type,
            [.. firm.StageConventions.Select(convention => new StageConventionDto(convention.Stage, convention.CapitalAtRisk))]);
    }
}
