using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The reduce route's HTTP contract (gh#928) — how each outcome reaches the operator.
/// </summary>
/// <remarks>
/// The distinction that matters most: <b>only a verified reduction is a 200</b>. A partial close the venue
/// accepted while still reporting the original size, a side flip, and an unreachable venue must never present as
/// "done" — the operator would trust a smaller exposure than they actually hold. A non-strict-partial request
/// (at or beyond the open size) is a 400, told apart from a transient.
/// </remarks>
public class PositionReduceEndpointTests
{
    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static VenueId Projectx => VenueId.Parse("projectx");
    private static VenueAccountId VenueAccount => VenueAccountId.Create(Projectx, "9001");
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueContractId ContractId => VenueContractId.Create(Projectx, Contract);

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionReduceEndpointTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(VenueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Returns(new ResolvedContract(ContractId, InstrumentId.Parse("MES")));
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([Position(3)]);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).Returns(Position(2));
    }

    private static PositionSnapshot Position(int net) => new(VenueAccount, ContractId, net, new Price(5_000m));

    private void ReducesTo(int net) =>
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).Returns(Position(net));

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private PositionReduceService Service() =>
        new(Context(), _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            NullLogger<PositionReduceService>.Instance);

    private async Task<Guid> SeedAccountAsync()
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
            CredentialKey = "topstep-main",
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

    private Task<IResult> Reduce(Guid id, string instrument, int quantity) =>
        PositionReconciliationEndpoints.ReduceAsync(
            id, instrument, new PositionReduceRequest(quantity), Service(), CancellationToken.None);

    // --- Endpoint-level validation ---

    [Fact]
    public async Task ReduceAsync_ShouldReturnNotFound_WhenTheAccountIsNotTheCallers()
    {
        // Nothing seeded, so the account does not exist for this caller (R-20).
        IResult result = await Reduce(Guid.NewGuid(), "MES", 1);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReturnBadRequest_WhenTheInstrumentIsNotParseable()
    {
        IResult result = await Reduce(Guid.NewGuid(), "   ", 1);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ReduceAsync_ShouldReturnBadRequest_WhenTheQuantityIsNotPositive(int quantity)
    {
        // A non-positive reduce is meaningless — a client mistake, and it never reaches the venue.
        IResult result = await Reduce(Guid.NewGuid(), "MES", quantity);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // --- The outcome -> status mapping: the safety-critical half ---

    [Fact]
    public async Task ReduceAsync_ShouldReturn200_WhenTheVenueReportsAVerifiedReduction()
    {
        Guid accountId = await SeedAccountAsync();
        ReducesTo(2); // long 3 -> long 2

        IResult result = await Reduce(accountId, "MES", 1);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReturn409_WhenTheVenueStillReportsTheOriginalSize()
    {
        // Never 200: an accepted-but-unchanged partial close must not present as done.
        Guid accountId = await SeedAccountAsync();
        ReducesTo(3); // unchanged

        IResult result = await Reduce(accountId, "MES", 1);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReturn409_WhenTheVenueIsUnreachable()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ReducePositionAsync(
                A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        IResult result = await Reduce(accountId, "MES", 1);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReturn400_WhenTheReduceIsNotStrictlyPartial()
    {
        // Reducing by the whole position (or more) is not a partial: a 400, distinct from a transient, so the
        // operator reaches for the exit control (which also cancels the protective legs) instead.
        Guid accountId = await SeedAccountAsync();

        IResult result = await Reduce(accountId, "MES", 3); // holds 3

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }
}
