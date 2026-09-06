using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Audit;

/// <summary>
/// The EF-backed <see cref="IAuditLog"/> (gh#220): appends audit entries to the
/// <see cref="TradingCopilotDbContext"/> in their <b>own</b> <c>SaveChanges</c>. A caller commits its safety
/// action first, so this second save is a separate transaction — its failure cannot roll the action back.
/// </summary>
/// <remarks>
/// Writes are not touched by the R-20 query filter (that scopes reads only), so a record is persisted with
/// whatever owner it already carries — the affected entity's owner, set by the caller — even from background
/// plumbing running with no user context.
/// </remarks>
public sealed class AuditLog : IAuditLog
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the audit log over the scoped application context.</summary>
    /// <param name="database">The application context (scoped).</param>
    public AuditLog(TradingCopilotDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public async Task WriteAsync(IReadOnlyCollection<AuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        _database.AuditRecords.AddRange(records);
        try
        {
            await _database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The mirror of TimescaleEventLog's detach, and here for the same reason (gh#1143): this log shares the
            // caller's scoped context, so a refused audit row left in `Added` would poison whatever that request
            // saves next -- turning a swallowed audit failure into someone else's lost write. A secondary write
            // that cannot fail its caller must not be able to fail the caller's NEXT one either.
            foreach (AuditRecord record in records)
            {
                _database.Entry(record).State = EntityState.Detached;
            }

            throw;
        }
    }
}
