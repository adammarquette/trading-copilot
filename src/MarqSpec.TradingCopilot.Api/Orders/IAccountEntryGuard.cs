using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// Serializes entry-transmits for one account, so two concurrent entries cannot both size against the same
/// flat-account snapshot and both reach the venue. Opened for send-vs-send (gh#531); extended to the take and
/// conditional-fire paths, and to counting <see cref="OrderStatus.Taking"/>, once a stranded take became
/// recoverable rather than a dead-lock (gh#589).
/// </summary>
/// <remarks>
/// <para>
/// The direct-send paths (<c>POST /accounts/{id}/orders</c> and <c>/send-as-is</c>) each run
/// <c>ComposeAsync</c>, which builds a fresh flat-account <c>RiskContext</c> sized off the venue's raw balance with
/// <c>UnrealizedPnL = 0</c>. A <b>working</b> order changes neither positions nor balance until it fills, so
/// composition reserves <b>nothing</b> for one that is already outstanding: two requests arriving together each see
/// the same flat account, each pass the R-5/R-16 gate at full size, and <b>both transmit</b> — up to twice the
/// approved risk on one account. This is the send-path sibling of the take-path double-transmit
/// <see cref="IStagedOrderClaim"/> closes (gh#530).
/// </para>
/// <para>
/// <b>Two things are needed, and this seam is one of them.</b> Serialization alone is insufficient: even run
/// strictly one-after-another, the second send still composes a flat snapshot that cannot honestly size around the
/// first's outstanding order. So the fix is a pair — a deterministic <i>no-stacking check</i> (refuse a send when
/// the account already holds an entry in <c>Working</c> / <c>PartiallyFilled</c> / <c>Taking</c>) run
/// <b>inside</b> the callback this guard wraps, plus this guard to hold a per-account lock <b>across</b> that check
/// and the place. The check answers "is the account already committed?"; the lock ensures two racers cannot both
/// answer "no" before either has journaled. Under the real guard the check therefore runs inside the lock, which
/// is the entire point of putting it in the callback rather than at the endpoint.
/// </para>
/// <para>
/// <b>Why a seam rather than a line of code in the endpoint.</b> The lock has to be evaluated by the database —
/// two HTTP requests share no change tracker, so nothing in process can arbitrate them. That means a Postgres
/// advisory lock and raw SQL, and <b>neither is supported by the EF in-memory provider</b> the unit tier runs on.
/// Putting it inline would make the send endpoints unrunnable under the unit tests and take the existing send
/// guards down with it. The seam keeps those guards running against a fake (which simply invokes the callback —
/// the in-memory provider is single-threaded, so the deterministic no-stacking check is what the unit tier
/// proves), and puts the real serialization where it can actually be proven: the container-backed Postgres tier,
/// where QA drives two truly-concurrent sends and asserts exactly one places.
/// </para>
/// <para>
/// <b>Why a session advisory lock over a pinned connection, and not an explicit transaction.</b> The lock is
/// taken with <c>pg_advisory_lock</c> (session scope) on a connection pinned for the callback's duration, then
/// released with <c>pg_advisory_unlock</c> and the connection closed. A session lock — not a transaction
/// (<c>pg_advisory_xact_lock</c>) — means the journal's <c>SaveChanges</c> keeps its single-statement auto-commit,
/// opening <b>no enclosing DB transaction</b>. It does <b>not</b> follow that there is no new orphan window: the
/// connection is held <i>pinned and idle across the venue round-trip</i>, so a mid-call backend drop would fail the
/// journaling write and leave a placed order un-journaled — the pre-existing place-then-journal window at higher
/// probability, an <b>accepted cost</b> documented with its mitigations in ADR-0007's 2026-08-02 update, not a
/// property this lock removes. If the process or connection dies mid-callback, Postgres releases a session lock
/// automatically when the backend connection drops, so a crash cannot strand the account locked forever.
/// </para>
/// <para>
/// <b>On <c>hashtext</c> collisions.</b> The account id is mapped to the lock's <c>bigint</c> key via
/// <c>hashtext</c> (int4). A collision therefore makes two <i>different</i> accounts occasionally share one lock —
/// which only over-serializes them (a momentary, harmless wait); it can <b>never</b> let two sends on the
/// <i>same</i> account run concurrently, so it is never a correctness loss. Enforcement lives below the model
/// (R-5/R-11/R-16, ADR-0007): the gate and this guard hold the limit, the LLM only proposes.
/// </para>
/// <para>
/// <b>The unlock is cleanup, so it does not take the caller's token.</b> The guarded callback spans a venue
/// round-trip, so a request aborting mid-callback is ordinary. On the operator send / take paths that token is
/// <c>RequestAborted</c>; releasing the lock on it would throw before the <c>pg_advisory_unlock</c> was ever sent,
/// and the pooled connector would go back to Npgsql still holding it — the account then refusing its next
/// entry-transmit (or, on the try-lock path, surfacing a spurious "busy") until that connector's next use resets
/// it. The paragraph above is what makes that recoverable rather than permanent; it is <i>not</i> what makes it
/// prompt, which is why the explicit unlock runs on <see cref="CancellationToken.None"/>.
/// </para>
/// </remarks>
public interface IAccountEntryGuard
{
    /// <summary>
    /// Runs <paramref name="transmit"/> under an exclusive per-account lock, so entry-transmits for one account
    /// serialize. The no-stacking check that keeps a second entry from stacking on an outstanding order lives
    /// inside <paramref name="transmit"/>, so under the real guard it runs while the lock is held.
    /// </summary>
    /// <typeparam name="T">
    /// The transmit tail's result — <see cref="IResult"/> for the operator send/take endpoints, the firing outcome
    /// for the conditional-fire watcher. The guard adds serialization, never an outcome.
    /// </typeparam>
    /// <param name="database">
    /// The caller's context, whose connection is pinned for the lock's lifetime so the SESSION lock, the callback's
    /// own queries + SaveChanges, and the unlock all run on <b>one backend</b>. The operator paths pass their
    /// request-scoped context; the conditional-fire watcher passes its per-owner (R-20-scoped) context.
    /// </param>
    /// <param name="accountId">The account whose entry-transmits serialize.</param>
    /// <param name="transmit">The check-then-place-then-journal tail to run exclusively for the account.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Whatever <paramref name="transmit"/> returns — this guard adds serialization, never an outcome.</returns>
    Task<T> RunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid accountId, Func<Task<T>> transmit, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="RunExclusiveAsync"/>, but <b>non-blocking</b>: if another session already holds the account's
    /// lock it does NOT wait — it runs <paramref name="onBusy"/> instead. For the conditional-fire watcher, whose
    /// single background loop must never block on an operator's in-flight send / take (gh#589): a busy account simply
    /// leaves the conditional Pending to re-decide on the next quote, so one account's operator latency cannot stall
    /// firing / cancel / expiry for every other account.
    /// </summary>
    /// <typeparam name="T">The transmit tail's result (the firing outcome for the watcher).</typeparam>
    /// <param name="database">The caller's context, whose connection is pinned for the lock's lifetime.</param>
    /// <param name="accountId">The account whose entry-transmits serialize.</param>
    /// <param name="transmit">The check-then-place-then-journal tail to run exclusively once the lock is acquired.</param>
    /// <param name="onBusy">The result to return WITHOUT running <paramref name="transmit"/> when the lock is held elsewhere.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><paramref name="transmit"/>'s result when the lock was acquired, otherwise <paramref name="onBusy"/>'s.</returns>
    Task<T> TryRunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid accountId, Func<Task<T>> transmit, Func<T> onBusy,
        CancellationToken cancellationToken);
}

/// <summary>
/// The database-evaluated guard — a Postgres session advisory lock held over a pinned connection, which is the
/// only thing two separate requests both observe.
/// </summary>
public sealed class AccountEntryGuard : IAccountEntryGuard
{
    /// <inheritdoc />
    public async Task<T> RunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid accountId, Func<Task<T>> transmit, CancellationToken cancellationToken)
    {
        // Pin the CALLER'S connection so the SESSION lock, the callback's own queries + SaveChanges, and the unlock
        // all run on one backend -- a session lock is bound to the connection that took it. Stateless by design: the
        // context is a parameter, not a captured field, so the operator paths lock over their request-scoped context
        // and the conditional-fire watcher over its per-owner context, each keeping lock and work on one backend. The
        // interpolated id is a bound parameter (ExecuteSqlAsync parameterizes a FormattableString), never concatenated.
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await database.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_lock(hashtext({accountId.ToString()}))", cancellationToken);
            try
            {
                return await transmit();
            }
            finally
            {
                // CancellationToken.None, deliberately, and it is load-bearing rather than tidy. The callback spans
                // a venue round-trip, so a request aborting mid-transmit is ordinary, not exotic -- and the
                // caller's token is already cancelled by the time this finally runs. Passing it would make
                // ExecuteSqlAsync throw BEFORE sending pg_advisory_unlock, leaving the session lock held on a
                // connector that CloseConnectionAsync then returns to Npgsql's pool. The account would then refuse
                // its next entry-transmit with a spurious "busy" until that connector happened to be reused.
                // Releasing a lock is cleanup: it must run to completion regardless of why the work above stopped.
                await database.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock(hashtext({accountId.ToString()}))", CancellationToken.None);
            }
        }
        finally
        {
            // Belt-and-braces behind the explicit unlock above, and stated precisely because the two mechanisms are
            // not the same. Returning the connection to the pool resets it (Npgsql's DISCARD ALL, which releases
            // session advisory locks) and a backend that genuinely DROPS releases them outright -- so a crash
            // between lock and unlock cannot strand the account locked forever. What it does NOT give is
            // promptness: the pooled reset is deferred to that connector's next use, which is exactly why the
            // unlock above must not be allowed to be skipped. (CloseConnectionAsync takes no token -- closing a
            // connection is not a cancellable operation in EF Core.)
            await database.Database.CloseConnectionAsync();
        }
    }

    /// <inheritdoc />
    public async Task<T> TryRunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid accountId, Func<Task<T>> transmit, Func<T> onBusy,
        CancellationToken cancellationToken)
    {
        // Non-blocking sibling of RunExclusiveAsync (gh#589): pg_try_advisory_lock returns immediately with whether it
        // took the lock, so the single-threaded conditional-fire watcher never waits on an operator's in-flight send /
        // take on the same account -- which would stall firing / cancel / expiry for EVERY account. A busy account
        // returns onBusy() (the fire stays Pending, re-decides next quote). Same key + same one-backend discipline.
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            bool acquired = await database.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtext({accountId.ToString()})) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!acquired)
            {
                return onBusy();
            }

            try
            {
                return await transmit();
            }
            finally
            {
                // CancellationToken.None -- same reasoning as RunExclusiveAsync's unlock. A stranded lock here is
                // more visible, not less: the next try-lock on this account observes it as held and returns
                // onBusy() -- for the conditional-fire watcher, a fire deferred to the next quote for no reason.
                await database.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock(hashtext({accountId.ToString()}))", CancellationToken.None);
            }
        }
        finally
        {
            // Belt-and-braces behind the explicit unlock above -- see RunExclusiveAsync's finally for why the two
            // mechanisms (pool reset / dropped backend vs. the explicit unlock) are not the same, and why only the
            // latter gives promptness.
            await database.Database.CloseConnectionAsync();
        }
    }
}
