using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api;

/// <summary>Startup work run once when the host boots: apply EF migrations and (optionally) seed the first operator.</summary>
public static class StartupTasks
{
    /// <summary>
    /// Applies pending migrations, then — if <c>Bootstrap:Email</c> / <c>Bootstrap:Password</c> are configured and no
    /// such user exists — seeds a first operator so the invitation-only flow has someone to issue the first invite.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>A task that completes when startup work is done.</returns>
    public static async Task MigrateAndBootstrapAsync(WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.MigrateAsync();

        string? email = app.Configuration["Bootstrap:Email"];
        string? password = app.Configuration["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (await database.Users.AnyAsync(user => user.Email == email))
        {
            return;
        }

        IPasswordHasher passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
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
