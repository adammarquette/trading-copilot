using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The operator's per-position exit (gh#656, R-11) — the blotter's "exit this position" control.
/// </summary>
/// <remarks>
/// <para>
/// It reuses <see cref="IOrderExecutor.ClosePositionAsync"/>, the same native-first close auto-flatten, the
/// watchdog and the kill switch all drive. A second close path would be a second thing to get wrong on the one
/// action that reduces real exposure.
/// </para>
/// <para>
/// It is a <b>reducing</b> action, so it is deliberately **not** gated on the kill switch: engaging the kill
/// switch stops new risk, and the operator must still be able to close what is already open (the property the
/// gh#657 safety strip states out loud).
/// </para>
/// </remarks>
public class PositionExitServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");
    private static VenueAccountId VenueAccount => VenueAccountId.Create(Projectx, "9001");
    private const string Contract = "CON.F.US.MES.U26";

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionExitServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(VenueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        Closes(Flat());
    }

    private static PositionSnapshot Position(int net) =>
        new(VenueAccount, VenueContractId.Create(Projectx, Contract), net, new Price(5_000m));

    private static PositionSnapshot Flat() => Position(0);

    private void Closes(PositionSnapshot after) =>
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .Returns(after);

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private PositionExitService Service(string credentialKey = "topstep-main") =>
        new(Context(), _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            NullLogger<PositionExitService>.Instance);

    private async Task<Guid> SeedAccountAsync(string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = Context();
        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        seed.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        await seed.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task ExitAsync_ShouldCloseThroughTheVenue_WhenThePositionIsOpen()
    {
        Guid accountId = await SeedAccountAsync();

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.Flat);
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExitAsync_ShouldReportStillOpen_WhenTheVenueReportsExposureAfterTheClose()
    {
        // The close returning is not the same as the position being gone. Verified against what the venue reports
        // AFTER the attempt, never assumed from the call succeeding -- the discipline auto-flatten already holds.
        Guid accountId = await SeedAccountAsync();
        Closes(Position(2));

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.StillOpen);
        result.NetQuantity.Should().Be(2);
    }

    [Fact]
    public async Task ExitAsync_ShouldReportUnreachable_WhenTheVenueThrows()
    {
        // A venue fault is NOT "it closed". Reporting success here would tell the operator their exposure is gone
        // while it is still live -- the single worst answer this path can give.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.Unreachable);
    }

    [Fact]
    public async Task ExitAsync_ShouldReturnNull_WhenTheAccountIsNotTheCallers()
    {
        // R-20: another operator's account is not found, not forbidden -- the same shape every account-scoped read
        // uses, so this path leaks no existence.
        Guid accountId = await SeedAccountAsync();

        await using TradingCopilotDbContext other = Context(Guid.NewGuid());
        PositionExitService service = new(
            other, _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            NullLogger<PositionExitService>.Instance);

        (await service.ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ExitAsync_ShouldRefuse_WhenTheAccountBelongsToAnotherProcessCredentialSet()
    {
        // One credential set per process (ADR-0015). Closing a position on an account this process does not serve
        // would be acting on someone else's venue session.
        Guid accountId = await SeedAccountAsync(credentialKey: "someone-else");

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.Unreachable);
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExitAsync_ShouldReportUnreachable_WhenTheVenueNoLongerReportsTheAccount()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Returns<IReadOnlyList<VenueAccount>>([]);

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.Unreachable);
    }
}
