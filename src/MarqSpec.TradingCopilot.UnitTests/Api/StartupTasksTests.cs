using MarqSpec.TradingCopilot.Api;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Operator bootstrap and recovery (R-18, ADR-0017 operator lifecycle). The critical property: a forgotten
/// password is recoverable from the environment the operator already controls — <b>without</b> replacing the
/// user row, because a new user id would orphan every R-20-scoped row in the workspace.
/// </summary>
public class StartupTasksTests
{
    private readonly PasswordHasher _hasher = new();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(Guid.NewGuid()));
    }

    [Fact]
    public async Task Bootstrap_ShouldSeedTheOperator_WhenNoneExists()
    {
        await using TradingCopilotDbContext context = Context();

        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "first-password", resetPassword: false);

        User seeded = await context.Users.SingleAsync();
        seeded.Email.Should().Be("op@local");
        _hasher.Verify(seeded.PasswordHash, "first-password").Should().BeTrue();
        seeded.IsPrimaryOperator.Should().BeTrue(); // the bootstrap account IS the primary -- the only inviter (gh#128)
    }

    [Fact]
    public async Task Bootstrap_ShouldNotTouchAnExistingOperator_WithoutTheResetFlag()
    {
        await using TradingCopilotDbContext context = Context();
        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "original", resetPassword: false);

        // A later restart with a different env password must not silently change the credential.
        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "stale-env-value", resetPassword: false);

        User user = await context.Users.SingleAsync();
        _hasher.Verify(user.PasswordHash, "original").Should().BeTrue();
        _hasher.Verify(user.PasswordHash, "stale-env-value").Should().BeFalse();
    }

    [Fact]
    public async Task Bootstrap_ShouldResetThePassword_WhenTheFlagIsSet_AndKeepTheSameUser()
    {
        await using TradingCopilotDbContext context = Context();
        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "forgotten", resetPassword: false);
        Guid originalId = (await context.Users.SingleAsync()).Id;

        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "recovered", resetPassword: true);

        User user = await context.Users.SingleAsync();

        // Same row, same id — the workspace's R-20-scoped rows still belong to this operator. A reseeded user
        // with a new id would strand every firm, convention, and (later) order behind the default-deny filter.
        user.Id.Should().Be(originalId);
        _hasher.Verify(user.PasswordHash, "recovered").Should().BeTrue();
        _hasher.Verify(user.PasswordHash, "forgotten").Should().BeFalse();
    }

    [Fact]
    public async Task Bootstrap_ShouldSeedNormally_WhenTheFlagIsSetButNoUserExists()
    {
        await using TradingCopilotDbContext context = Context();

        await StartupTasks.BootstrapOperatorAsync(context, _hasher, "op@local", "first", resetPassword: true);

        User seeded = await context.Users.SingleAsync();
        _hasher.Verify(seeded.PasswordHash, "first").Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("op@local", null)]
    [InlineData("  ", "password")]
    public async Task Bootstrap_ShouldDoNothing_WhenUnconfigured(string? email, string? password)
    {
        await using TradingCopilotDbContext context = Context();

        await StartupTasks.BootstrapOperatorAsync(context, _hasher, email, password, resetPassword: true);

        (await context.Users.AnyAsync()).Should().BeFalse();
    }
}
