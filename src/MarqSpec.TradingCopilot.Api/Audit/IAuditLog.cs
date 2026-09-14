using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Audit;

/// <summary>
/// Appends immutable <see cref="AuditRecord"/> entries (data dictionary §12, engineering §9, ADR-0007, gh#220).
/// </summary>
/// <remarks>
/// A <b>secondary</b> write by design: a caller performs and commits its safety action first, then records it
/// here. Callers therefore invoke this <b>after</b> the action has already persisted and tolerate a failure —
/// the audit trail matters, but it must never be the reason a flatten, orphan, or re-arm did not complete.
/// Each record carries its own owner (<see cref="AuditRecord.UserId"/>), so the log is written across operators
/// from background plumbing that has no ambient user context (R-20).
/// </remarks>
public interface IAuditLog
{
    /// <summary>Appends the given audit entries in their own unit of work.</summary>
    /// <param name="records">The entries to append; an empty collection is a no-op.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task WriteAsync(IReadOnlyCollection<AuditRecord> records, CancellationToken cancellationToken);
}
