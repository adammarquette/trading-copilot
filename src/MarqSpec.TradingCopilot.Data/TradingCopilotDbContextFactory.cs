using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the context for migrations without the application host.
/// The connection string here is a design-time placeholder — <c>migrations add</c> does not connect to a database.
/// </summary>
public sealed class TradingCopilotDbContextFactory : IDesignTimeDbContextFactory<TradingCopilotDbContext>
{
    /// <summary>Creates a context configured for the Npgsql provider at design time.</summary>
    /// <param name="args">Command-line arguments (unused).</param>
    /// <returns>A context suitable for migration tooling.</returns>
    public TradingCopilotDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseNpgsql("Host=localhost;Database=tradingcopilot;Username=postgres;Password=postgres")
                .Options;

        return new TradingCopilotDbContext(options, new DesignTimeCurrentUser());
    }

    private sealed class DesignTimeCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
