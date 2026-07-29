namespace MarqSpec.TradingCopilot.Data.Tenancy;

/// <summary>
/// An <see cref="ICurrentUser"/> pinned to a specific owner, for a <b>background</b> data context with no request
/// user (gh#385 / gh#431). It is the R-20 scope a per-owner unit of work runs under — the firing-watcher pattern:
/// discover owners with <c>IgnoreQueryFilters</c>, then do each owner's work under <c>new OwnerUser(owner)</c> so
/// every read and write is that owner's alone.
/// </summary>
/// <remarks>
/// Shared by the trigger scan (which stamps firings / suggestions) and the AIUsage ledger (which stamps spend rows)
/// so the same tenancy identity backs both, rather than each background writer re-declaring its own.
/// </remarks>
/// <param name="UserId">The owner this context is scoped to.</param>
public sealed record OwnerUser(Guid UserId) : ICurrentUser;
