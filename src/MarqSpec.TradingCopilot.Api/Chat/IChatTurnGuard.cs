using MarqSpec.TradingCopilot.Data;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Chat;

/// <summary>
/// Serializes chat turns for one conversation, so a conversation has <b>at most one turn in flight</b> (gh#1106,
/// R-6). The chunk stream's only correlation key is the conversation (<c>RealtimeChatChunk</c>), and this seam is
/// what turns that from an assumption into a guarantee the server keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>What goes wrong without it.</b> <c>ChatEndpoints.TurnAsync</c> refuses only an exact
/// <c>(ConversationId, Sequence)</c> collision, so two screens on one conversation — the multi-screen desk ADR-0006
/// / ADR-0021 exist to support — can have two turns in flight at once. Their deltas then reach the operator's
/// connections as <b>one undifferentiated stream</b>: a chunk carries the conversation id and the text, nothing
/// else, so no client can attribute a delta or tell which turn a settled message terminates. The operator watching
/// sees their draft reset and re-grow partway through an answer.
/// </para>
/// <para>
/// <b>Why refusing rather than attributing.</b> The alternative was a per-turn correlation id on the chunk and the
/// message, which is a hub wire contract change with an ADR-0021 entry, backward tolerance, and a draft-keyed-by-turn
/// rework in the SPA — and it would let the client render two live drafts in one thread, which is a confusing read
/// whatever the draft does. The operator decided the product does not want concurrent turns on one conversation at
/// all (recorded on gh#1106). So the second turn is refused with a <b>409</b> carrying a displayable reason, and no
/// id goes on the wire.
/// </para>
/// <para>
/// <b>Why a database lock, and why a seam.</b> The race is the whole problem: a check-then-act would let two
/// requests both observe "no turn in flight" and both proceed. Two HTTP requests share no change tracker, so nothing
/// in process can arbitrate them — the lock has to be evaluated by the database, which means raw SQL the EF
/// in-memory provider the unit tier runs on does not support. The seam keeps the endpoint's other guards runnable
/// under a fake (which simply invokes the callback) and puts the real serialization where QA can prove it on the
/// container-backed Postgres tier. This is the <see cref="Orders.IAccountEntryGuard"/> pattern (gh#531), reused
/// deliberately rather than reinvented.
/// </para>
/// <para>
/// <b>Non-blocking, because waiting would be the wrong answer.</b> A chat turn can run for tens of seconds. Queueing
/// the second request behind the first would leave a screen hanging on a turn it will then answer twice over; the
/// honest response is an immediate refusal the operator can read. So the lock is taken with
/// <c>pg_try_advisory_lock</c> and a busy conversation runs <c>onBusy</c>.
/// </para>
/// <para>
/// <b>Why a session lock over a pinned connection.</b> A session lock (not <c>pg_advisory_xact_lock</c>) leaves the
/// callback's <c>SaveChanges</c> calls their single-statement auto-commit and opens no enclosing transaction; the
/// connection is pinned for the callback's duration so the lock, the callback's queries and the unlock all run on
/// <b>one backend</b>. It is held idle across the model call — the accepted cost of the guarantee, and the same
/// shape <see cref="Orders.IAccountEntryGuard"/> accepts across a venue round-trip. If the process or the
/// connection dies mid-turn, Postgres releases a session lock when the backend drops, so a crash can never strand a
/// conversation locked forever — which is exactly why this is a lock rather than an "in flight" column somebody
/// would have to sweep.
/// </para>
/// <para>
/// <b>On the key.</b> The conversation id is mapped through <c>hashtext</c> (int4) into the <b>two-argument</b>
/// advisory-lock space under a fixed class key. Postgres keeps the one-argument (<c>bigint</c>) and two-argument
/// (<c>int, int</c>) spaces separate, so a chat turn can never collide with
/// <see cref="Orders.IAccountEntryGuard"/>'s per-account lock and be refused for an order the operator is sending
/// on an unrelated account. Within this space a <c>hashtext</c> collision can only make two <i>different</i>
/// conversations share one lock — a spurious refusal on a second conversation, never two turns running on the
/// <i>same</i> one, so it is never a loss of the guarantee.
/// </para>
/// </remarks>
public interface IChatTurnGuard
{
    /// <summary>
    /// Runs <paramref name="turn"/> under an exclusive per-conversation lock, taken <b>non-blocking</b>: if another
    /// turn already holds the conversation this does NOT wait — it returns <paramref name="onBusy"/>'s result
    /// without running <paramref name="turn"/> at all.
    /// </summary>
    /// <typeparam name="T">The turn's result — <c>IResult</c> for the chat turn endpoint.</typeparam>
    /// <param name="database">
    /// The caller's context, whose connection is pinned for the lock's lifetime so the SESSION lock, the callback's
    /// own queries + SaveChanges, and the unlock all run on <b>one backend</b>.
    /// </param>
    /// <param name="conversationId">The conversation whose turns serialize.</param>
    /// <param name="turn">The whole turn — persist, ground, stream, ledger, append — to run exclusively.</param>
    /// <param name="onBusy">The result to return WITHOUT running <paramref name="turn"/> when a turn is in flight.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><paramref name="turn"/>'s result when the lock was acquired, otherwise <paramref name="onBusy"/>'s.</returns>
    Task<T> TryRunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid conversationId, Func<Task<T>> turn, Func<T> onBusy,
        CancellationToken cancellationToken);
}

/// <summary>
/// The database-evaluated guard — a Postgres session advisory lock held over a pinned connection, which is the only
/// thing two separate chat-turn requests both observe.
/// </summary>
public sealed class ChatTurnGuard : IChatTurnGuard
{
    /// <summary>
    /// The advisory-lock class key for chat turns (ASCII <c>CHAT</c>), the first argument of the two-argument
    /// advisory-lock space. It namespaces this guard's locks away from every other advisory lock in the deployment.
    /// </summary>
    private const int ChatTurnLockClass = 0x43484154;

    /// <inheritdoc />
    public async Task<T> TryRunExclusiveAsync<T>(
        TradingCopilotDbContext database, Guid conversationId, Func<Task<T>> turn, Func<T> onBusy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(onBusy);

        // Pin the CALLER'S connection so the SESSION lock, the callback's own queries + SaveChanges, and the unlock
        // all run on one backend -- a session lock is bound to the connection that took it. Stateless by design: the
        // context is a parameter, not a captured field. The interpolated values are bound parameters (SqlQuery /
        // ExecuteSqlAsync parameterize a FormattableString), never concatenated.
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            bool acquired = await database.Database
                .SqlQuery<bool>(
                    $"SELECT pg_try_advisory_lock({ChatTurnLockClass}, hashtext({conversationId.ToString()})) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!acquired)
            {
                return onBusy();
            }

            try
            {
                return await turn();
            }
            finally
            {
                await database.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock({ChatTurnLockClass}, hashtext({conversationId.ToString()}))",
                    cancellationToken);
            }
        }
        finally
        {
            // Closing the connection also releases the session lock even if the unlock above never ran (a crash
            // between lock and unlock), so a conversation can never be stranded locked. (CloseConnectionAsync takes
            // no token -- closing a connection is not a cancellable operation in EF Core.)
            await database.Database.CloseConnectionAsync();
        }
    }
}
