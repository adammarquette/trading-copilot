using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The shared pre-merge integration-test host (gh#121): boots the real API pipeline against a
/// throwaway PostgreSQL container running the same image as the local compose stack, so the real
/// EF migrations, check constraints, unique indexes, and DB-level guards (e.g. the gh#96 mode
/// trigger) are live in every run. Isolation is by construction — a fresh container per suite,
/// destroyed on dispose; an operator's persistent volumes are unreachable. Suites needing extra
/// service overrides (e.g. a venue stub) subclass and override <see cref="ConfigureTestServices"/>.
/// </summary>
public class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The compose stack's database image — kept identical for local ≡ CI ≡ compose parity.</summary>
    public const string DatabaseImage = "timescale/timescaledb-ha:pg17";

    /// <summary>The bootstrap operator's email, seeded when the host boots.</summary>
    public const string OperatorEmail = "operator@example.com";

    /// <summary>The bootstrap operator's password — a fabricated test value, not a secret.</summary>
    public const string OperatorPassword = "Password123!";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder(DatabaseImage).Build();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The only data-layer intervention is the connection string: the production
        // AddTradingCopilotData/UseNpgsql registration stays intact, so the pipeline under test is
        // the shipped one — including StartupTasks.MigrateAndBootstrapAsync's real MigrateAsync().
        builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-that-is-long-enough-32bytes");
        builder.UseSetting("Jwt:Issuer", "trading-copilot-tests");
        builder.UseSetting("Jwt:Audience", "trading-copilot-tests");
        builder.UseSetting("Bootstrap:Email", OperatorEmail);
        builder.UseSetting("Bootstrap:Password", OperatorPassword);

        builder.ConfigureServices(ConfigureTestServices);
    }

    /// <summary>
    /// Suite-specific service overrides; the base registers nothing. The QA contract permits a
    /// test double here for the venue seam only, and it must be adversarial — feed inputs, never
    /// the production-computed answer.
    /// </summary>
    /// <param name="services">The API's service collection.</param>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>Starts the database container before the host is (lazily) built.</summary>
    /// <returns>A task that completes when the container accepts connections.</returns>
    public Task InitializeAsync() => _database.StartAsync();

    /// <summary>Disposes the host, then stops and removes the database container.</summary>
    /// <returns>A task that completes when both are gone.</returns>
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }
}
