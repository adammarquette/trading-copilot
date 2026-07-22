using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A persisted AI trade suggestion (data dictionary §6) — the <b>spine</b> only (gh#7): identity, the proposed
/// trade, its mode, and its lifecycle state.
/// </summary>
/// <remarks>
/// <para>
/// Deferred deliberately: confidence, validity window, strategy linkage, rationale + retrieval references
/// (VEC — lands with pgvector), version / supersedes, and dispositions — they arrive with the suggestion
/// pipeline (R-4/R-8).
/// </para>
/// <para>
/// <see cref="Mode"/> is <b>mode-guarded</b> (R-14): a DB constraint trigger refuses a suggestion whose mode
/// disagrees with its account's persisted mode at write time, and <see cref="TradingMode.Undeclared"/> is
/// refused outright by a check constraint — an undeclared account cannot be traded, so nothing should ever be
/// suggested on it.
/// </para>
/// </remarks>
public class Suggestion : IUserOwned
{
    /// <summary>The suggestion's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the suggestion targets — the parent whose mode the R-14 guard compares against.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The venue contract the suggestion is for (e.g. <c>CON.F.US.MES.U26</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The proposed direction. <c>required</c>: the zero value is a real side, never a default.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The proposed size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>The proposed entry price.</summary>
    public required decimal EntryPrice { get; set; }

    /// <summary>The proposed protective stop price.</summary>
    public required decimal StopPrice { get; set; }

    /// <summary>The proposed target price (a single target in the spine; ladders come with R-4).</summary>
    public required decimal TargetPrice { get; set; }

    /// <summary>
    /// The mode the suggestion was issued under (R-14). Guarded: must equal the account's persisted mode at
    /// write time, and <see cref="TradingMode.Undeclared"/> is refused outright.
    /// </summary>
    public required TradingMode Mode { get; set; }

    /// <summary>The lifecycle state. <see cref="SuggestionState.Unknown"/> is refused by a DB check.</summary>
    public required SuggestionState State { get; set; }

    /// <summary>When the suggestion was issued.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
