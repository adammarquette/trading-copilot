using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>Which operator position action a journal entry attests to (gh#1143).</summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an uninitialised kind must
/// never masquerade as a real recorded action.
/// </remarks>
public enum PositionActionKind
{
    /// <summary>Not an action — the refusable zero. Never recorded.</summary>
    Unknown = 0,

    /// <summary>The operator's per-position <b>full exit</b> (gh#656) — <c>POST …/positions/{instrument}/exit</c>.</summary>
    Exit = 1,

    /// <summary>The operator's <b>sized partial close</b> (gh#928) — <c>POST …/positions/{instrument}/reduce</c>.</summary>
    Reduce = 2,
}

/// <summary>
/// One operator position-action attempt and its verified outcome, as the journal records it (gh#1143).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every outcome is an entry, not only the successful ones.</b> A refusal, a busy account, a held path and an
/// unreachable venue are the entries an incident is actually reconstructed from — the happy path is the one case
/// venue truth could have told you about anyway.
/// </para>
/// <para>
/// <b><see cref="RequestedQuantity"/> is the fact nothing else holds.</b> After a reduce, how many contracts the
/// operator asked to take off is <b>not reconstructable from venue truth</b>: the venue reports only the resulting
/// position, and a long 5 that became a long 2 looks identical whether 3 was requested, or 1 was requested and a
/// stop took 2, or 3 was requested twice against an unsettled read (gh#1143, gh#928's settle-window hazard).
/// </para>
/// </remarks>
public sealed record PositionActionEntry
{
    /// <summary>Which action was requested — the full exit or the sized reduce.</summary>
    public required PositionActionKind Action { get; init; }

    /// <summary>The affected account's owning operator (R-20) — the entry is stamped with, and visible only to, them.</summary>
    public required Guid OwnerUserId { get; init; }

    /// <summary>The platform account id the action was requested against.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>The venue's own key for that account, so the entry reads against venue-side records too.</summary>
    public required string VenueAccountKey { get; init; }

    /// <summary>The instrument the operator named (e.g. <c>MES</c>).</summary>
    public required string Instrument { get; init; }

    /// <summary>
    /// The venue contract the instrument resolved to, or <see langword="null"/> when the attempt never got that far
    /// (a credential-key mismatch, a held path, a busy account, an absent roster entry).
    /// </summary>
    public string? Contract { get; init; }

    /// <summary>
    /// How many contracts the operator asked to take off, for a <see cref="PositionActionKind.Reduce"/>.
    /// <see langword="null"/> for an <see cref="PositionActionKind.Exit"/>, which carries no size — it asks for flat.
    /// </summary>
    public int? RequestedQuantity { get; init; }

    /// <summary>
    /// The signed net quantity the venue reported <b>before</b> the attempt, when it was read.
    /// <see langword="null"/> when it was not: the full exit does not read the position before closing it, and the
    /// paths that send nothing never get that far.
    /// </summary>
    public int? NetQuantityBefore { get; init; }

    /// <summary>
    /// The signed net quantity the venue reported <b>after</b> the attempt. <see langword="null"/> when no exposure
    /// was established — an unreachable venue, an indeterminate refusal, or a path that sent nothing. It is left
    /// null rather than zeroed: a <c>0</c> here would fabricate a flat out of an outage (gh#929).
    /// </summary>
    public int? NetQuantityAfter { get; init; }

    /// <summary>
    /// The outcome name the operator was told — <c>Flat</c> / <c>StillOpen</c> / <c>Unreachable</c> for the exit;
    /// <c>Reduced</c> / <c>Unconfirmed</c> / <c>NotReduced</c> / <c>Refused</c> / <c>ExceedsPosition</c> /
    /// <c>AccountBusy</c> / <c>HeldPracticeOnly</c> / <c>Unreachable</c> for the reduce. Recorded as the name the
    /// endpoint returned, so the trail and the response can never tell different stories.
    /// </summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// Records what the operator's per-position <b>exit</b> (gh#656) and <b>reduce</b> (gh#928) asked for and what
/// happened — the durable trace both paths transmitted real orders without (gh#1143, R-8 / R-9 / R-11).
/// </summary>
/// <remarks>
/// <para>
/// <b>A secondary write, subordinate to the action</b> — the <see cref="Audit.IAuditLog"/> discipline (gh#220),
/// stated here as a hard property rather than a convention: this seam <b>never throws</b>. The venue action has
/// already happened by the time it is called, so a journal fault must never change the outcome the operator is
/// told, and must never turn a verified close into an error the caller reads as a failure.
/// </para>
/// <para>
/// <b>It takes no cancellation token, deliberately.</b> The action being recorded is already done — a real order
/// reached a real venue — so the record must not be dropped because the HTTP client hung up mid-response. This is
/// the same reasoning <c>AccountEntryGuard</c> applies to its advisory unlock: a completion step whose whole job is
/// to run <i>after</i> the cancellable work must not take the cancelled token. Structurally, a caller cannot pass a
/// cancelled token because there is no parameter to pass one through.
/// </para>
/// </remarks>
public interface IPositionActionJournal
{
    /// <summary>Records one attempt and its outcome. Never throws.</summary>
    /// <param name="entry">The attempt, its request and its outcome.</param>
    /// <param name="occurredAt">When the attempt resolved.</param>
    Task RecordAsync(PositionActionEntry entry, DateTimeOffset occurredAt);
}

/// <summary>The call-site belt for <see cref="IPositionActionJournal"/> (gh#1143).</summary>
/// <remarks>
/// <b>Braces to the seam's belt, and shared so the pair cannot drift.</b> The shipped implementation is contracted
/// never to throw and guards each of its two writes separately — but a contract is a promise, and the property it
/// promises (a record of a safety action must never be able to <i>prevent</i>, <i>fail</i> or <i>alter</i> that
/// action) is the one that must not depend on a promise. Any implementation of the seam, present or future, is
/// therefore also swallowed here. It is one shared helper rather than a <c>try</c> in each service, because two
/// hand-copied fault boundaries around one shape is precisely how this pair of endpoints drifted in the first place.
/// </remarks>
public static class PositionActionJournalExtensions
{
    /// <summary>Records one attempt, absorbing any fault the journal implementation lets escape.</summary>
    /// <param name="journal">The journal.</param>
    /// <param name="entry">The attempt, its request and its outcome.</param>
    /// <param name="occurredAt">When the attempt resolved.</param>
    /// <param name="logger">The caller's logger — the last resort when the record cannot be written at all.</param>
    public static async Task RecordSafelyAsync(
        this IPositionActionJournal journal,
        PositionActionEntry entry,
        DateTimeOffset occurredAt,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await journal.RecordAsync(entry, occurredAt);
        }
        catch (Exception error)
        {
            // Cancellation included: the caller is holding a verified outcome for an order that already reached a
            // real venue, and turning that into an aborted request would be strictly worse than losing a row.
            logger.LogError(
                error,
                "Could not record the operator {Action} of {Instrument} on account {Account} (outcome {Outcome}); "
                + "the action itself is unaffected (gh#1143).",
                entry.Action,
                entry.Instrument,
                entry.AccountId,
                entry.Outcome);
        }
    }
}
