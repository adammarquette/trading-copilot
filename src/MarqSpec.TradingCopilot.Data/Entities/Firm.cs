using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// An account provider the operator trades with — a prop firm or a brokerage (data dictionary "Firm", gh#76).
/// </summary>
/// <remarks>
/// Operator-owned (<see cref="IUserOwned"/>): a firm record carries the operator's own declarations and, in a
/// later increment, their credentials — so it is scoped per user by the default-deny filter (R-20). Two operators
/// each trading Topstep hold two distinct <see cref="Firm"/> rows, invisible to one another.
/// </remarks>
public class Firm : IUserOwned
{
    /// <summary>The firm record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The firm's name (e.g. <c>Topstep</c>, <c>Apex Trader Funding</c>).</summary>
    public required string Name { get; set; }

    /// <summary>Whether this is a prop firm (staged) or a brokerage (live/paper only).</summary>
    public FirmType Type { get; set; }

    /// <summary>
    /// The operator's per-stage declarations of what each stage means here (gh#60). Compose the domain
    /// <see cref="Domain.Venue.FirmConventions"/> via <see cref="FirmConventionsMapping.ToConventions"/>. Empty
    /// until the operator declares — every stage then resolves to <see cref="Domain.Venue.TradingMode.Undeclared"/>.
    /// </summary>
    public ICollection<FirmStageConvention> StageConventions { get; set; } = [];
}
