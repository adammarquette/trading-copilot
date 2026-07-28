using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>Dependency-injection registration for the data layer.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TradingCopilotDbContext"/> against Postgres. The context resolves its
    /// <c>ICurrentUser</c> from DI, so the caller must register one (server-side) for per-user isolation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The Postgres connection string.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTradingCopilotData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TradingCopilotDbContext>(options =>
            // UseVector enables the pgvector type mapping (gh#109). Without it the Embedding column has no CLR
            // mapping and the context cannot even be constructed -- so this is not optional wiring.
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        return services;
    }
}
