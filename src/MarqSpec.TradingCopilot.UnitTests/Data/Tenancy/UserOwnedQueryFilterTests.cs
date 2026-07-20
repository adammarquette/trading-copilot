using FakeItEasy;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Tenancy;

public class UserOwnedQueryFilterTests
{
    [Fact]
    public void Query_ShouldReturnOnlyTheCurrentUsersRows()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        DbContextOptions<TenantTestContext> options = InMemoryOptions();
        Seed(options, userA, userB);

        using TenantTestContext asA = new(options, CurrentUser(userA));

        asA.Owned.Should().ContainSingle().Which.UserId.Should().Be(userA);
    }

    [Fact]
    public void Query_ShouldReturnNothing_WhenThereIsNoCurrentUser()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        DbContextOptions<TenantTestContext> options = InMemoryOptions();
        Seed(options, userA, userB);

        using TenantTestContext noUser = new(options, CurrentUser(Guid.Empty));

        noUser.Owned.Should().BeEmpty();
    }

    private static void Seed(DbContextOptions<TenantTestContext> options, params Guid[] owners)
    {
        using TenantTestContext seed = new(options, CurrentUser(Guid.Empty));
        foreach (Guid owner in owners)
        {
            seed.Owned.Add(new OwnedRow { Id = Guid.NewGuid(), UserId = owner });
        }

        seed.SaveChanges();
    }

    private static DbContextOptions<TenantTestContext> InMemoryOptions()
    {
        return new DbContextOptionsBuilder<TenantTestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ICurrentUser CurrentUser(Guid id)
    {
        ICurrentUser currentUser = A.Fake<ICurrentUser>();
        A.CallTo(() => currentUser.UserId).Returns(id);
        return currentUser;
    }

    private sealed class OwnedRow : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class TenantTestContext(DbContextOptions<TenantTestContext> options, ICurrentUser currentUser)
        : TenantDbContext(options, currentUser)
    {
        public DbSet<OwnedRow> Owned => Set<OwnedRow>();
    }
}
