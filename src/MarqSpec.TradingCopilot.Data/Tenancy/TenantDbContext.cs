using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data.Tenancy;

/// <summary>
/// A <see cref="DbContext"/> that enforces fail-closed operator scoping (R-20 / ADR-0017): every entity implementing
/// <see cref="IUserOwned"/> automatically receives a <b>default-deny</b> per-user query filter. The current user's
/// id is held in a context field and re-evaluated by EF per query, so one context can never read another user's
/// rows, and with no user context (<see cref="System.Guid.Empty"/>) the filter matches nothing.
/// </summary>
public abstract class TenantDbContext : DbContext
{
    private readonly Guid _currentUserId;

    /// <summary>Creates the context, capturing the current user for query scoping.</summary>
    /// <param name="options">The context options.</param>
    /// <param name="currentUser">The authenticated-user accessor (server-side).</param>
    protected TenantDbContext(DbContextOptions options, ICurrentUser currentUser)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        _currentUserId = currentUser.UserId;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        MethodInfo apply = typeof(TenantDbContext)
            .GetMethod(nameof(ApplyUserOwnedFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IUserOwned).IsAssignableFrom(entityType.ClrType))
            {
                apply.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ApplyUserOwnedFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IUserOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.UserId == _currentUserId);
    }
}
