using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        // Attach any ISaveChangesInterceptor registered by an upper layer (gh#295: the gate-decisions metric counts
        // GateDecisionRecord rows as they are written). Resolved from DI so the Data layer stays free of any Api
        // reference; when none is registered this is an empty, harmless no-op.
        services.AddDbContext<TradingCopilotDbContext>((serviceProvider, options) =>
            options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetServices<ISaveChangesInterceptor>()));
        return services;
    }
}
