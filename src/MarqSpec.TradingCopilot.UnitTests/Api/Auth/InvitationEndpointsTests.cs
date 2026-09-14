using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Auth;

/// <summary>
/// The invitation-issuance guard (gh#128): only the <b>primary operator</b> may issue invitations. The gh#127
/// probe showed any authenticated user could — an accepted invitee could chain invitations into uncontrolled
/// account creation on a single-operator deployment (ADR-0017). Declared, not derived: the primary account is
/// a persisted flag the bootstrap seeds, never an inference from creation order.
/// </summary>
public class InvitationEndpointsTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _primary = Guid.NewGuid();
    private readonly Guid _invitee = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid asUser)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(asUser));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private async Task SeedUsersAsync()
    {
        await using TradingCopilotDbContext context = Context(_primary);
        context.Users.Add(new User
        {
            Id = _primary,
            Email = "operator@local",
            PasswordHash = "hash",
            DisplayName = "The Operator",
            IsPrimaryOperator = true,
        });
        context.Users.Add(new User
        {
            Id = _invitee,
            Email = "mentee@local",
            PasswordHash = "hash",
            DisplayName = "Accepted Invitee",
            IsPrimaryOperator = false,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task IssueInvitation_ShouldIssue_WhenTheCallerIsThePrimaryOperator()
    {
        await SeedUsersAsync();
        await using TradingCopilotDbContext context = Context(_primary);

        IResult result = await AuthEndpoints.IssueInvitationAsync(
            new IssueInvitationRequest("new-mentee@example.com"),
            new FixedUser(_primary), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        Invitation stored = await context.Invitations.SingleAsync();
        stored.IssuedByUserId.Should().Be(_primary);
    }

    [Fact]
    public async Task IssueInvitation_ShouldForbid_WhenTheCallerIsNotThePrimaryOperator()
    {
        await SeedUsersAsync();
        await using TradingCopilotDbContext context = Context(_invitee);

        IResult result = await AuthEndpoints.IssueInvitationAsync(
            new IssueInvitationRequest("chained@example.com"),
            new FixedUser(_invitee), context, CancellationToken.None);

        // The gh#128 fix: an accepted invitee holds a valid token but is NOT the primary account -- chaining
        // is refused and nothing is persisted.
        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        (await context.Invitations.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IssueInvitation_ShouldForbid_WhenTheCallerNoLongerExists()
    {
        await SeedUsersAsync();
        Guid ghost = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(ghost);

        IResult result = await AuthEndpoints.IssueInvitationAsync(
            new IssueInvitationRequest("ghost@example.com"),
            new FixedUser(ghost), context, CancellationToken.None);

        // A valid-but-orphaned token (deleted user) fails closed the same way -- absent is never primary.
        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        (await context.Invitations.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptInvite_ShouldCreateANonPrimaryUser()
    {
        await SeedUsersAsync();
        (string token, string tokenHash) = InvitationTokens.Generate();
        await using (TradingCopilotDbContext seed = Context(_primary))
        {
            seed.Invitations.Add(new Invitation
            {
                Id = Guid.NewGuid(),
                Email = "fresh@example.com",
                TokenHash = tokenHash,
                IssuedByUserId = _primary,
                Status = InvitationStatus.Pending,
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
            });
            await seed.SaveChangesAsync();
        }

        IPasswordHasher hasher = A.Fake<IPasswordHasher>();
        A.CallTo(() => hasher.Hash(A<string>._)).Returns("hashed");
        ITokenIssuer issuer = A.Fake<ITokenIssuer>();
        A.CallTo(() => issuer.Issue(A<User>._)).Returns("jwt");

        await using TradingCopilotDbContext context = Context(_primary);
        IResult result = await AuthEndpoints.AcceptInviteAsync(
            new AcceptInviteRequest(token, "NewPassword123!", "Fresh Mentee"),
            context, hasher, issuer, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context(_primary);
        User created = await reload.Users.SingleAsync(user => user.Email == "fresh@example.com");
        created.IsPrimaryOperator.Should().BeFalse(); // invited users are never the primary account (gh#128)
    }
}
