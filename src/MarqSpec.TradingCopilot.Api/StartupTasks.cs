using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api;

/// <summary>Startup work run once when the host boots: apply EF migrations and bootstrap the operator.</summary>
public static class StartupTasks
{
    /// <summary>
    /// Applies pending migrations, then bootstraps the operator from <c>Bootstrap:Email</c> /
    /// <c>Bootstrap:Password</c>: seeds them when absent, and — only behind the explicit
    /// <c>Bootstrap:ResetPassword</c> flag — resets an existing operator's password from the environment
    /// (the recovery path; R-18, ADR-0017 operator lifecycle).
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>A task that completes when startup work is done.</returns>
    public static async Task MigrateAndBootstrapAsync(WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.MigrateAsync();

        await BootstrapOperatorAsync(
            database,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            app.Configuration["Bootstrap:Email"],
            app.Configuration["Bootstrap:Password"],
            resetPassword: bool.TryParse(app.Configuration["Bootstrap:ResetPassword"], out bool reset) && reset);
    }

    /// <summary>
    /// Seeds the operator when no user with <paramref name="email"/> exists; with
    /// <paramref name="resetPassword"/> set, re-hashes <paramref name="password"/> onto the <b>existing</b> user
    /// instead.
    /// </summary>
    /// <param name="database">The application database.</param>
    /// <param name="passwordHasher">The credential hasher.</param>
    /// <param name="email">The bootstrap email; nothing happens when absent.</param>
    /// <param name="password">The bootstrap password; nothing happens when absent.</param>
    /// <param name="resetPassword">
    /// The explicit recovery opt-in. The operator controls the deployment, so the environment may deliberately
    /// reset the credential — but never silently: without this flag a stale env password cannot overwrite the
    /// stored one. The reset keeps the <b>same user row and id</b>, because a reseeded user with a new id would
    /// strand every R-20-scoped row in the workspace behind the default-deny filter.
    /// </param>
    /// <returns>A task that completes when the operator is bootstrapped.</returns>
    internal static async Task BootstrapOperatorAsync(
        TradingCopilotDbContext database,
        IPasswordHasher passwordHasher,
        string? email,
        string? password,
        bool resetPassword)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        User? existing = await database.Users.FirstOrDefaultAsync(user => user.Email == email);

        if (existing is not null)
        {
            if (resetPassword)
            {
                existing.PasswordHash = passwordHasher.Hash(password);
                await database.SaveChangesAsync();
            }

            return;
        }

        database.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = passwordHasher.Hash(password),
            DisplayName = "Operator",
            Status = UserStatus.Active,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await database.SaveChangesAsync();
    }
}
