using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// The application <see cref="DbContext"/>. Inherits the multi-user isolation model from
/// <see cref="TenantDbContext"/> — every <see cref="IUserOwned"/> entity gets a default-deny per-user query
/// filter (R-20 / ADR-0011).
/// </summary>
public class TradingCopilotDbContext : TenantDbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">The context options (provider, connection).</param>
    /// <param name="currentUser">The authenticated-user accessor used to scope every user-owned query.</param>
    public TradingCopilotDbContext(DbContextOptions<TradingCopilotDbContext> options, ICurrentUser currentUser)
        : base(options, currentUser)
    {
    }

    /// <summary>The tenant-root users.</summary>
    public DbSet<User> Users => Set<User>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.Email).IsUnique();
            user.Property(u => u.Email).HasMaxLength(320);
            user.Property(u => u.DisplayName).HasMaxLength(128);
        });
    }
}
