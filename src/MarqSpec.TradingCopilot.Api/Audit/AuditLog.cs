using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;

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
        await _database.SaveChangesAsync(cancellationToken);
    }
}
