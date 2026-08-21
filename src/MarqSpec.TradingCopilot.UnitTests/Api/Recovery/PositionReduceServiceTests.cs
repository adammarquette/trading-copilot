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
/// The operator's per-position reduce (gh#928, R-11) — the blotter's "take part of this position off" control, a
/// sized partial close toward flat.
/// </summary>
/// <remarks>
/// <para>
/// It rides <see cref="IOrderExecutor.ReducePositionAsync"/> (the venue's native sized partial close), NOT the
/// order-placement ladder: a reduce lowers exposure, and routing it as an opposing order would let the R-5 gate
/// refuse a risk-lowering action. Like the full exit it is not gated on the kill switch.
/// </para>
/// <para>
/// The two properties these tests defend: it is <b>strictly partial</b> (a request at or beyond the open size is
/// refused before the venue is touched, so a reduce can never become a bracket-stranding flatten), and <b>only a
/// verified reduction is success</b> (the outcome is read from venue truth after the attempt — a still-original
/// size, a side flip, or a fault is never reported as reduced).
/// </para>
/// </remarks>
public class PositionReduceServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");
    private static VenueAccountId VenueAccount => VenueAccountId.Create(Projectx, "9001");
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueContractId ContractId => VenueContractId.Create(Projectx, Contract);

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionReduceServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(VenueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Returns(new ResolvedContract(ContractId, InstrumentId.Parse("MES")));
        Holds(3);      // long 3 by default
        ReducesTo(2);  // the venue takes one off by default
    }

    private static PositionSnapshot Position(int net) =>
        new(VenueAccount, ContractId, net, new Price(5_000m));

    private void Holds(int net) =>
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(net == 0 ? [] : [Position(net)]);

    private void ReducesTo(int net) =>
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).Returns(Position(net));

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private PositionReduceService Service(string credentialKey = "topstep-main") =>
        new(Context(), _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            NullLogger<PositionReduceService>.Instance);

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

    private Task<PositionReduceResult?> Reduce(Guid accountId, int quantity) =>
        Service().ReduceAsync(accountId, InstrumentId.Parse("MES"), quantity, CancellationToken.None);

    [Fact]
    public async Task ReduceAsync_ShouldReportReduced_WhenTheVenueClosedExactlyTheRequestedAmount()
    {
        // Verified against what was ASKED, not just direction: long 3, reduce by 1, venue reports long 2 — exactly
        // the requested delta. That, and only that, is a clean Reduced.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(2);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Reduced);
        result.NetQuantity.Should().Be(2);
        A.CallTo(() => _venue.ReducePositionAsync(A<VenueAccountId>._, ContractId, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenTheVenueClosedLessThanRequested()
    {
        // The gh#928-review finding: a partial close that closed LESS than asked must NOT read as a clean reduction.
        // Asked to take 3 off a long 5 (expecting 2 left); the venue only closed 1, so 4 remain — more exposure than
        // the operator targeted. Not-done, with the true net (4), never a green Reduced they have to catch.
        Guid accountId = await SeedAccountAsync();
        Holds(5);
        ReducesTo(4);

        PositionReduceResult? result = await Reduce(accountId, 3);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(4);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenTheVenueClosedMoreThanRequested()
    {
        // The over-execution mirror (adversarial-review P2): a reduce-by-1 that came back FLAT closed MORE than
        // asked — a protective stop or a concurrent exit fired — so it is not the reduction the operator asked for
        // (they meant to keep 2 on). Not-done with the true net (0), never a clean "reduced" masking a dangling
        // bracket / unexpected flat. (OcoExitService cancels the legs on the flat; this makes the operator SEE it.)
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(0);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(0);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenTheVenueStillReportsTheOriginalSize()
    {
        // The core discipline: a partialClose the venue accepted while still reporting the ORIGINAL size is not a
        // reduction. Reporting it as done would tell the operator their exposure is smaller than it is.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(3);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(3);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenThePositionReversedSide()
    {
        // A side flip is a REVERSAL, not a reduction — however small its size. long 3 coming back short 1 has a
        // smaller magnitude, but it is new opposite exposure, and must never read as "reduced".
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(-1);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(-1);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReduceAShortTowardFlat_WhenTheVenueReportsSmallerShortExposure()
    {
        // Symmetry: a short reduces toward flat the same way — a smaller-magnitude short is a verified reduction.
        Guid accountId = await SeedAccountAsync();
        Holds(-4);
        ReducesTo(-1);

        PositionReduceResult? result = await Reduce(accountId, 3);

        result!.Outcome.Should().Be(PositionReduceOutcome.Reduced);
        result.NetQuantity.Should().Be(-1);
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheQuantityEqualsWhatIsOpen()
    {
        // Reducing by the WHOLE position is a full close — which belongs to the exit path, because a full close
        // cancels the protective OCO legs and this sized partial close does not. Refused before the venue is
        // touched, so a reduce can never silently become a bracket-stranding flatten.
        Guid accountId = await SeedAccountAsync();
        Holds(2);

        PositionReduceResult? result = await Reduce(accountId, 2);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(2);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheQuantityExceedsWhatIsOpen()
    {
        Guid accountId = await SeedAccountAsync();
        Holds(2);

        PositionReduceResult? result = await Reduce(accountId, 5);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenNothingIsOpen()
    {
        // Nothing to reduce is not a venue fault, and must not reach the venue: a reduce against a flat contract
        // would otherwise be a partialClose the gateway rejects as "no position", mislabelled as unreachable.
        Guid accountId = await SeedAccountAsync();
        Holds(0);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(0);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenTheVenueReduceThrows()
    {
        // A venue fault is NOT "it reduced". Reporting success here would tell the operator their exposure is
        // smaller while it may be unchanged -- the worst answer this path can give.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        A.CallTo(() => _venue.ReducePositionAsync(
                A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenReadingTheStartingPositionThrows()
    {
        // The before-read is on the same venue session; a fault reading it is just as unreachable, and must never
        // fall through to a reduce sized against a size we could not confirm.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReturnNull_WhenTheAccountIsNotTheCallers()
    {
        // R-20: another operator's account is not found, not forbidden.
        Guid accountId = await SeedAccountAsync();

        await using TradingCopilotDbContext other = Context(Guid.NewGuid());
        PositionReduceService service = new(
            other, _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            NullLogger<PositionReduceService>.Instance);

        (await service.ReduceAsync(accountId, InstrumentId.Parse("MES"), 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuse_WhenTheAccountBelongsToAnotherProcessCredentialSet()
    {
        // One credential set per process (ADR-0015): reducing on an account this process does not serve would be
        // acting on someone else's venue session.
        Guid accountId = await SeedAccountAsync(credentialKey: "someone-else");

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenTheVenueNoLongerReportsTheAccount()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>([]);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
    }

    [Fact]
    public async Task ReduceAsync_ShouldThrow_WhenTheQuantityIsNotPositive()
    {
        // A defensive guard: the endpoint rejects a non-positive quantity as a 400, but a mis-wired caller must
        // never reach the venue with a meaningless size.
        Guid accountId = await SeedAccountAsync();

        Func<Task> act = async () => await Reduce(accountId, 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
