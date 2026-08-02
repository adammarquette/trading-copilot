using MarqSpec.TradingCopilot.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// Serializes entry-transmits for one account, so two concurrent direct sends cannot both size against the same
/// flat-account snapshot and both reach the venue (gh#531).
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
/// (<c>pg_advisory_xact_lock</c>) — means the journal's <c>SaveChanges</c> auto-commits <b>exactly as it does
/// today</b>: no enclosing transaction is opened, so no new window is introduced in which a placed venue order
/// could be left un-journaled (no new orphan window versus the pre-fix path). If the process or connection dies
/// mid-callback, Postgres releases a session lock automatically when the backend connection drops, so a crash
/// cannot strand the account locked forever.
/// </para>
/// <para>
/// <b>On <c>hashtext</c> collisions.</b> The account id is mapped to the lock's <c>bigint</c> key via
/// <c>hashtext</c> (int4). A collision therefore makes two <i>different</i> accounts occasionally share one lock —
/// which only over-serializes them (a momentary, harmless wait); it can <b>never</b> let two sends on the
/// <i>same</i> account run concurrently, so it is never a correctness loss. Enforcement lives below the model
/// (R-5/R-11/R-16, ADR-0007): the gate and this guard hold the limit, the LLM only proposes.
/// </para>
/// </remarks>
public interface IAccountEntryGuard
{
    /// <summary>
    /// Runs <paramref name="transmit"/> under an exclusive per-account lock, so entry-transmits for one account
    /// serialize. The no-stacking check that keeps a second send from stacking on an outstanding order lives
    /// inside <paramref name="transmit"/>, so under the real guard it runs while the lock is held.
    /// </summary>
    /// <param name="accountId">The account whose entry-transmits serialize.</param>
    /// <param name="transmit">The check-then-place-then-journal tail to run exclusively for the account.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Whatever <paramref name="transmit"/> returns — this guard adds serialization, never an outcome.</returns>
    Task<IResult> RunExclusiveAsync(Guid accountId, Func<Task<IResult>> transmit, CancellationToken cancellationToken);
}

/// <summary>
/// The database-evaluated guard — a Postgres session advisory lock held over a pinned connection, which is the
/// only thing two separate requests both observe.
/// </summary>
public sealed class AccountEntryGuard : IAccountEntryGuard
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the guard over the scoped database.</summary>
    /// <param name="database">The scoped database whose connection is pinned for the lock's lifetime.</param>
    public AccountEntryGuard(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<IResult> RunExclusiveAsync(
        Guid accountId, Func<Task<IResult>> transmit, CancellationToken cancellationToken)
    {
        // Pin the connection so the SESSION lock, the callback's own queries + SaveChanges, and the unlock all run
        // on one backend -- a session lock is bound to the connection that took it. The interpolated id is a bound
        // parameter (ExecuteSqlAsync parameterizes a FormattableString), never concatenated into the SQL text.
        await _database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await _database.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_lock(hashtext({accountId.ToString()}))", cancellationToken);
            try
            {
                return await transmit();
            }
            finally
            {
                await _database.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock(hashtext({accountId.ToString()}))", cancellationToken);
            }
        }
        finally
        {
            // Closing the connection also releases the session lock even if the unlock above never ran (a crash
            // between lock and unlock), so the account can never be stranded locked. (CloseConnectionAsync takes no
            // token -- closing a connection is not a cancellable operation in EF Core.)
            await _database.Database.CloseConnectionAsync();
        }
    }
}
