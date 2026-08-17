using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A recomposition-suppression tombstone (gh#955): the durable record that an <see cref="Outcome"/> was
/// <b>hard-deleted</b>, so the <c>OutcomeJournalService</c> sweeps must not recompose it. Every outcome is a
/// projection of a live source — a closed <see cref="Trade"/> (gh#909) or a terminal <see cref="Suggestion"/>
/// (gh#939) — so removing the row alone does not stick: the next sweep (≤ one poll interval) re-derives it, with the
/// R-15 flags defaulting off, resurrecting it against the operator's confirmed removal. Both sweeps anti-join this
/// tombstone, so a suppressed source is never re-outcomed; the hard delete writes it in the <b>same unit of work</b>
/// as the remove.
/// </summary>
/// <remarks>
/// Keyed on the outcome's OWN recomposition source — <see cref="TradeId"/> when the outcome had a trade (the
/// closed-trade sweep's key), otherwise <see cref="SuggestionId"/> (the unfilled-suggestion sweep's key) — with
/// <b>exactly one</b> set, so it suppresses precisely the sweep that would otherwise re-derive the row.
/// <c>CK_OutcomeSuppressions_OneParent</c> enforces the one-key shape and a unique filtered index per key makes the
/// suppression idempotent. It dies <b>with</b> that source (Cascade foreign keys, mirroring <see cref="Outcome"/>),
/// so removing the operator's account carries the tombstone away too and never strands one against a gone trade or
/// suggestion.
/// </remarks>
public class OutcomeSuppression : IUserOwned
{
    /// <summary>The tombstone's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — stamped from the hard-deleted outcome.</summary>
    public Guid UserId { get; set; }

    /// <summary>The trade whose outcome was suppressed (the closed-trade sweep's key); null for an untaken suppression.</summary>
    public Guid? TradeId { get; set; }

    /// <summary>The suggestion whose untaken outcome was suppressed (the unfilled sweep's key); null for a trade suppression.</summary>
    public Guid? SuggestionId { get; set; }

    /// <summary>When the hard delete recorded the suppression.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
