using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>What a reduce attempt actually achieved, verified against venue truth rather than inferred (gh#928).</summary>
public enum PositionReduceOutcome
{
    /// <summary>Never a valid outcome — the fail-closed zero.</summary>
    Unknown = 0,

    /// <summary>
    /// The venue reports the position smaller by <b>exactly the requested amount</b>, same side — the reduction is
    /// verified against what was asked, not merely against direction.
    /// </summary>
    Reduced = 1,

    /// <summary>
    /// The position <b>moved</b>, but not by what was asked — <b>not done</b>. Covers <b>less</b> off than asked
    /// (a partial execution), <b>more</b> off than asked (a protective stop or a concurrent exit fired, up to and
    /// including all the way to flat), and a <b>side flip</b> (a reversal is never a reduction, however small its
    /// magnitude). Read from venue truth after the attempt.
    /// </summary>
    /// <remarks>
    /// The original size <b>still standing</b> is deliberately <i>not</i> here — that is
    /// <see cref="Unconfirmed"/>, because a close that has not settled is indistinguishable from one that did
    /// nothing, and reading it as "it did not happen" is what invites a re-send of a non-idempotent write. Like
    /// <see cref="Unconfirmed"/>, this outcome is <b>not</b> an invitation to re-issue: the close was transmitted.
    /// </remarks>
    NotReduced = 2,

    /// <summary>The venue could not be reached or does not serve this account. <b>Never read as reduced.</b></summary>
    Unreachable = 3,

    /// <summary>
    /// The requested size is not strictly less than what is open, so it is not a partial. A reduce must <b>leave
    /// part of the position on</b>: taking the whole thing off is a full close (which the exit path owns, together
    /// with its OCO-cancel), and taking more than is open is a sizing mistake. Both are refused here, before the
    /// venue is touched — a reduce must never silently become a flatten.
    /// </summary>
    ExceedsPosition = 4,

    /// <summary>
    /// The venue <b>answered in the negative and executed nothing</b> (a definitive refusal — gh#629's
    /// <see cref="VenueRefusalKind.Definitive"/>). Distinct from <see cref="Unreachable"/>, which claims the venue
    /// could not be reached, and from <see cref="Unconfirmed"/>, which claims nothing about what executed: here the
    /// exposure is <b>known</b>, and it is exactly what it was before the attempt.
    /// </summary>
    Refused = 5,

    /// <summary>
    /// The partial close <b>was transmitted</b> and its effect cannot be established — the venue accepted it but
    /// still reports the position unchanged, or refused it indeterminately. A market close is not instantaneous, so
    /// an unchanged read-back means <i>either</i> the fill has not settled <i>or</i> nothing executed, and the two
    /// are <b>indistinguishable</b> from here.
    /// </summary>
    /// <remarks>
    /// <b>This is not "try again".</b> A sized partial close is non-idempotent, so re-sending inside the settle
    /// window takes the size off <i>twice</i> — past flat, into an opposing position. The only safe response is to
    /// <b>re-read venue truth</b> and decide from what is actually there. It is a separate outcome from
    /// <see cref="NotReduced"/> precisely so a caller cannot treat "it did not move" as "it did not happen".
    /// </remarks>
    Unconfirmed = 6,

    /// <summary>
    /// Another transmit already holds this account's serialization lock, so the reduce did <b>not</b> run — nothing
    /// was sent. Refusing beats waiting: a second non-idempotent partial close must never stack on one in flight
    /// (the gh#531 hazard class), and the operator can re-issue deliberately once the account is free.
    /// </summary>
    AccountBusy = 7,

    /// <summary>
    /// The reduce is <b>held</b> and did not run: it is <b>practice-only</b> until both of its named pre-live
    /// conditions clear, and this account is not a practice account. Nothing was sent.
    /// </summary>
    /// <remarks>
    /// This is not a permanent risk policy about reducing exposure — it is the two holds gh#928 ships behind, made
    /// <b>structural instead of asserted</b>. A prose hold is a promise a future session can break silently; this
    /// is the same promise the compiler keeps. It lifts, in one line, when the gh#1012 bracket verification
    /// produces a finding <i>and</i> MarqSpec.Client.ProjectX#98 lands. Until then the operator's route to a
    /// smaller position on a funded account is the full exit (gh#656), which has neither hold.
    /// </remarks>
    HeldPracticeOnly = 8,
}

/// <summary>The result of an operator-initiated reduce (gh#928).</summary>
/// <param name="Outcome">What was achieved, verified against venue truth rather than inferred from the call.</param>
/// <param name="NetQuantity">
/// The signed exposure the venue reports <b>after</b> the attempt — or the current size, when the request was
/// refused before the venue was touched. <b><see langword="null"/> when the venue could not be reached</b>: an
/// exposure we could not read is unknown, and reporting it as <c>0</c> would fabricate a flat out of an outage,
/// which is the failure mode gh#929 exists to prevent. It is also <see langword="null"/> whenever the exposure was
/// never established — an <b>indeterminate</b> refusal (the venue answered, but not in a way that says what
/// executed), and the two outcomes that send nothing at all,
/// <see cref="PositionReduceOutcome.AccountBusy"/> and <see cref="PositionReduceOutcome.HeldPracticeOnly"/>. It
/// carries a real number only when one was actually read: <see cref="PositionReduceOutcome.Reduced"/>,
/// <see cref="PositionReduceOutcome.NotReduced"/>, <see cref="PositionReduceOutcome.Unconfirmed"/> from a
/// post-close read, <see cref="PositionReduceOutcome.Refused"/>, and
/// <see cref="PositionReduceOutcome.ExceedsPosition"/>.
/// </param>
public sealed record PositionReduceResult(PositionReduceOutcome Outcome, int? NetQuantity);

/// <summary>
/// The operator's per-position <b>reduce</b> (gh#928, R-11) — the blotter's "take part of this position off"
/// control, a sized partial close toward flat.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reduce is a close, not a send.</b> It rides <see cref="IOrderExecutor.ReducePositionAsync"/> — the venue's
/// native sized partial close — and deliberately <b>not</b> the order-placement ladder. An opposing order would be
/// a send, gated by R-5 / R-16 / R-12, and a gate refusing a risk-<i>lowering</i> action would be the wrong answer.
/// As a reducing action it is, like the full exit (gh#656), <b>not</b> gated on the kill switch: engaging the kill
/// switch stops new risk; closing what is already open must keep working.
/// </para>
/// <para>
/// <b>Strictly partial, on purpose.</b> The requested size must be strictly less than what is open. Reducing by the
/// whole position is a full close — which belongs to the exit path, because a full close cancels the protective OCO
/// legs (gh#183, gh#656) and a sized partial close does not. Letting a reduce equal the position would route a
/// flatten through a path that leaves a dangling protective order, so it is refused
/// (<see cref="PositionReduceOutcome.ExceedsPosition"/>) before the venue is touched.
/// </para>
/// <para>
/// <b>Only a verified reduction is success.</b> The outcome is read from what the venue reports <i>after</i> the
/// attempt (ADR-0013): the position smaller by <b>exactly the requested amount</b>, same side, is
/// <see cref="PositionReduceOutcome.Reduced"/>. Nothing else is, and <i>how</i> it is not done is <b>named</b>,
/// because a sized partial close is non-idempotent and a caller that collapses these into one retry is itself the
/// hazard: the venue accepting the close while still reporting the original size — or refusing it indeterminately —
/// is <see cref="PositionReduceOutcome.Unconfirmed"/> (transmitted, effect unestablished, <b>re-read, never
/// re-send</b>); a position that moved but not by what was asked is
/// <see cref="PositionReduceOutcome.NotReduced"/>; a venue that answered in the negative and executed nothing is
/// <see cref="PositionReduceOutcome.Refused"/>; and an unreachable venue is
/// <see cref="PositionReduceOutcome.Unreachable"/>. None of them is ever <i>reduced</i>. Reporting anything but the
/// exact reduction as done would leave the operator to notice from the net quantity that they got something other
/// than what they asked for.
/// </para>
/// <para>
/// <b>Known limitation — the protective bracket is left as it is, and the venue's behaviour is unverified.</b> The
/// copilot's attached safety bracket carries no size field (<c>{ticks, type}</c>, sized to the realized fill on
/// attach; gh#293), and this reduce does not resize it. Whether ProjectX auto-reduces a position-linked bracket on
/// a partial close <b>has not been observed</b> — gh#1012 scaffolded that verification but it has never been run —
/// so a resting stop may still cover the <i>original</i> quantity and, on trigger, overshoot the remainder into an
/// opposing position. Which of auto-resize / refuse-the-desyncing-reduce / warn-loudly answers that is a policy
/// gh#928 §⚠️ reserves for the operator, and none of them is implemented here.
/// </para>
/// <para>
/// <b>Known limitation 2 — the partial close is auto-retried below this layer.</b> The ProjectX client's resilience
/// pipeline excludes exactly one route from transient-fault retry, <c>POST /api/Order/place</c> (ADR-0007, gh#673),
/// on the reasoning that every other route is a read or an idempotent write. <b>A sized partial close is neither.</b>
/// <c>partialCloseContract(1)</c> sent twice takes two contracts off, so a lost acknowledgement — a transport fault,
/// or a <c>5xx</c> returned <i>after</i> the gateway already executed — can silently replay the write up to three
/// more times before this service's read-back ever runs. On a small position that overshoots past flat into an
/// <b>opposing</b> one. Widening the client's exclusion is a change to the vendored client repo
/// (MarqSpec.Client.ProjectX#98), not something this layer can enforce: the retry happens inside the HTTP pipeline,
/// underneath <c>IProjectXApiClient</c>. Until it lands, this is a <b>named hold condition</b>, not a residual risk
/// somebody may forget. The verified-reduction rule below still refuses to call an over-reduced position done — it
/// reports the true, wrong net quantity — but it cannot undo the second send.
/// </para>
/// <para>
/// <b>Both holds point the same way:</b> this path is <b>practice-only</b>, and is not to be trusted on a funded
/// account until the gh#1012 gate produces a finding <i>and</i> MarqSpec.Client.ProjectX#98 lands.
/// </para>
/// </remarks>
public sealed class PositionReduceService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IAccountEntryGuard _accountGuard;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly ILogger<PositionReduceService> _logger;

    /// <summary>Creates the reduce service.</summary>
    /// <param name="database">The scoped database (R-20 applies).</param>
    /// <param name="venueFactory">Builds a venue for the connection's firm conventions.</param>
    /// <param name="accountGuard">
    /// The per-account transmit lock (gh#531). Required, not optional: without it two concurrent reduces both size
    /// against the same pre-reduce snapshot and both transmit.
    /// </param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="journal">
    /// The durable record of what was asked and what happened (gh#1143). <b>Required, not optional</b>: after a
    /// reduce, the requested quantity is not reconstructable from venue truth, so a silently-absent journal would
    /// lose the one fact this path cannot recover.
    /// </param>
    /// <param name="logger">The logger.</param>
    public PositionReduceService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IAccountEntryGuard accountGuard,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IPositionActionJournal journal,
        ILogger<PositionReduceService> logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _database = database;
        _venueFactory = venueFactory;
        _accountGuard = accountGuard;
        _projectXOptions = projectXOptions;
        _logger = logger;
    }

    /// <summary>
    /// Reduces one instrument's position on an account by <paramref name="quantity"/> contracts, at market.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="instrument">The instrument to reduce.</param>
    /// <param name="quantity">The positive number of contracts to take off (validated at the endpoint too).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The outcome, or <see langword="null"/> when the account is not found or not the caller's (R-20) — a 404,
    /// the same shape every account-scoped read uses, so this path leaks no existence.
    /// </returns>
    public async Task<PositionReduceResult?> ReduceAsync(
        Guid accountId,
        InstrumentId instrument,
        int quantity,
        CancellationToken cancellationToken)
    {
        // The endpoint rejects a non-positive quantity as a 400; guard defensively so a mis-wired caller can never
        // reach the venue with a meaningless size.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        Account? account = await _database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null; // not found / not owned (R-20)
        }

        Connection? connection = await _database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null
            || !string.Equals(connection.CredentialKey, _projectXOptions.Value.CredentialKey, StringComparison.Ordinal))
        {
            // One credential set per process (ADR-0015). Reducing on an account this process does not serve would
            // be acting through someone else's venue session.
            return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
        }

        // PRACTICE-ONLY, ENFORCED (gh#928). The reduce ships behind two named holds -- the unverified
        // post-partial-close bracket behaviour (gh#1012, never run) and the client-side auto-retry of this
        // non-idempotent write (MarqSpec.Client.ProjectX#98). Both say "not on a funded account", and until this
        // guard existed both said it only in prose. A hold nothing enforces is a promise the next session can break
        // without noticing, which is exactly how gh#1012 came to be read as cleared when it had never run.
        //
        // It refuses Undeclared and Live alike, so it is strictly stronger than R-14 while it stands -- but it is
        // NOT a ruling on whether R-14 binds reducing actions in general (no reducing path consults
        // TradingModePolicy: not the exit, not auto-flatten, not the kill switch). That question outlives these
        // holds and is the operator's; see ADR-0007's 2026-09-05 update. Refusing here costs the operator nothing
        // they cannot do another way: the full exit carries neither hold.
        if (account.Mode is not TradingMode.Practice)
        {
            _logger.LogWarning(
                "Reduce refused on account {Account}: the reduce is practice-only until gh#1012 and "
                + "MarqSpec.Client.ProjectX#98 clear, and this account's mode is {Mode} (gh#928).",
                accountId,
                account.Mode);
            return new PositionReduceResult(PositionReduceOutcome.HeldPracticeOnly, null);
        }

        // Everything from the before-read to the read-back runs under the account's transmit lock (gh#531), and
        // NON-BLOCKING: if another transmit already holds it, this returns AccountBusy having sent nothing. Waiting
        // would be worse than refusing -- a sized partial close is non-idempotent, so a second one stacking on one
        // in flight takes the size off twice, and the strict-partial guard cannot see a close that has not settled.
        // Refusing lets the operator re-issue deliberately, against a settled read.
        return await _accountGuard.TryRunExclusiveAsync(
            _database,
            accountId,
            () => ReduceUnderLockAsync(account, instrument, quantity, connection.Id, cancellationToken),
            () => new PositionReduceResult(PositionReduceOutcome.AccountBusy, null),
            cancellationToken);
    }

    /// <summary>
    /// The venue half of the reduce, run while this account's transmit lock is held.
    /// </summary>
    /// <param name="account">The caller's account row (already ownership- and credential-checked).</param>
    /// <param name="instrument">The instrument to reduce.</param>
    /// <param name="quantity">The positive number of contracts to take off.</param>
    /// <param name="connectionId">The connection whose firm conventions build the venue.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome, read from venue truth after the attempt.</returns>
    private async Task<PositionReduceResult?> ReduceUnderLockAsync(
        Account account,
        InstrumentId instrument,
        int quantity,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        // Captured before the venue is asked, so a definitive refusal can report the TRUE exposure instead of
        // claiming it is unknown. Null until the before-read succeeds.
        int? beforeQuantity = null;

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connectionId, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
            }

            // The same practice-only hold, re-checked against the roster the venue just reported. The row read
            // above is a persisted derivation that can lag a firm-conventions change; this one cannot. Cheap --
            // the roster read already happened -- and it means a stale row can never wave a funded account
            // through a path that is held.
            if (venueAccount.Mode is not TradingMode.Practice)
            {
                _logger.LogWarning(
                    "Reduce refused on account {Account}: the venue reports mode {Mode}, and the reduce is "
                    + "practice-only until gh#1012 and MarqSpec.Client.ProjectX#98 clear (gh#928).",
                    account.Id,
                    venueAccount.Mode);
                return new PositionReduceResult(PositionReduceOutcome.HeldPracticeOnly, null);
            }

            ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);

            // BEFORE: venue truth for this contract. Load-bearing for BOTH the strict-partial guard and the
            // verified-reduction check -- without the starting size neither is possible. Deliberately no local
            // belief and no IsActive-style filter (gh#929): a filtered exposure read fabricates a flat.
            IReadOnlyList<PositionSnapshot> before = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
            PositionSnapshot? position = before.FirstOrDefault(candidate => candidate.Contract == resolved.Contract);
            int openQuantity = position?.NetQuantity ?? 0;
            beforeQuantity = openQuantity;
            int beforeMagnitude = Math.Abs(openQuantity);

            // Strict partial: the request must LEAVE part of the position on. quantity >= what is open is either a
            // full close (which belongs to the exit path, with its OCO-cancel) or a sizing mistake -- refused here,
            // before the venue is touched, so a reduce can never silently become a flatten that strands a bracket.
            if (quantity >= beforeMagnitude)
            {
                return new PositionReduceResult(PositionReduceOutcome.ExceedsPosition, openQuantity);
            }

            PositionSnapshot after = await venue.ReducePositionAsync(
                venueAccount.Id, resolved.Contract, quantity, cancellationToken);

            // Verified against what was ASKED, not merely against direction (gh#928 §2 of the ratification list --
            // exact-delta, the card's own proposed default, still awaiting the operator's sign-off). Success is the
            // venue reporting the position smaller by EXACTLY `quantity`, same side. The strict-partial guard above
            // makes the target `beforeMagnitude - quantity` at least 1, so a same-side match can never be flat.
            if (Math.Sign(after.NetQuantity) == Math.Sign(openQuantity)
                && Math.Abs(after.NetQuantity) == beforeMagnitude - quantity)
            {
                return new PositionReduceResult(PositionReduceOutcome.Reduced, after.NetQuantity);
            }

            // TRANSMITTED, and the position has NOT MOVED. A market close is not instantaneous, so this is either a
            // fill that has not settled or a close that did nothing -- indistinguishable from here. It is reported
            // as Unconfirmed rather than folded into NotReduced precisely so no caller reads "it did not move" as
            // "it did not happen" and re-sends: the send is non-idempotent, so a retry inside the settle window
            // takes the size off twice, past flat into an opposing position. Re-read venue truth instead.

            if (after.NetQuantity == openQuantity)
            {
                return new PositionReduceResult(PositionReduceOutcome.Unconfirmed, after.NetQuantity);
            }

            // The position DID move, but not by what was asked: less off (a partial execution), more off (a stop or
            // a concurrent exit fired, up to flat), or a side flip. Not done -- and, like Unconfirmed, not an
            // invitation to re-send. The operator sees the true net and decides from it.
            return new PositionReduceResult(PositionReduceOutcome.NotReduced, after.NetQuantity);
        }
        catch (VenueRefusalException refusal) when (refusal.Kind == VenueRefusalKind.Definitive)
        {
            // The venue ANSWERED, in the negative, and executed nothing (gh#629). Calling that "unreachable" would
            // state two falsehoods at once -- that the venue was not reached, and that the exposure is unknowable --
            // so it is its own outcome, carrying the pre-attempt size, which is still the true one.
            _logger.LogWarning(
                refusal,
                "The venue refused to reduce {Instrument} on account {Account}; the position is unchanged (gh#928).",
                instrument,
                account.Id);
            return new PositionReduceResult(PositionReduceOutcome.Refused, beforeQuantity);
        }
        catch (VenueRefusalException refusal)
        {
            // Indeterminate -- the fail-safe default (gh#629): the close MAY be live. Never "nothing happened", and
            // never an invitation to re-send a non-idempotent write.
            _logger.LogError(
                refusal,
                "The reduce of {Instrument} on account {Account} was transmitted and its fate is unknown; it must "
                + "not be re-sent (gh#928).",
                instrument,
                account.Id);
            return new PositionReduceResult(PositionReduceOutcome.Unconfirmed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away -- abort, do not manufacture a business outcome. Narrow on purpose: a venue
            // timeout also surfaces as an OperationCanceledException, but carrying HttpClient's own internal token,
            // so it falls through to the fault handler below and reports Unreachable rather than escaping as an
            // aborted request.
            throw;
        }
        catch (Exception error) when (error is not NotSupportedException)
        {
            // NotSupportedException is deliberately NOT caught: it is the seam's fail-loud default for a venue that
            // cannot size a partial close (R-17), and laundering it into "Unreachable" would tell the operator the
            // venue was down when the truth is that this venue cannot do this at all. It must surface.
            _logger.LogError(
                error,
                "Operator reduce of {Instrument} on account {Account} could not be completed — the position may be "
                + "unchanged, or partly reduced (gh#928).",
                instrument,
                account.Id);
            return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
        }
    }
}
