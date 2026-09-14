using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The <c>GET /accounts/{id}/positions</c> shape (gh#193): what the venue-truth position reconcile hands a caller
/// at the HTTP boundary.
/// </summary>
/// <remarks>
/// The invariant that must not drift is the <b>404 on an unowned account</b> (R-20 default-deny — existence itself
/// is not disclosed). The <c>?instrument=</c> scope (gh#772) adds two distinct client-mistake <b>400</b>s the
/// boundary must name apart from an unreachable venue (which stays a declared-unknown <c>200</c>): a syntactically
/// malformed symbol, and a well-formed one the venue has <i>no contract</i> for.
/// </remarks>
public class PositionReconciliationEndpointsTests
{
    private const string CredentialKey = "topstep-main";

    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IProjectXVenueFactory _venueFactory = A.Fake<IProjectXVenueFactory>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionReconciliationEndpointsTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venueFactory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Returns<IReadOnlyList<VenueAccount>>(
                [new VenueAccount(VenueAccountId.Create(Venue, "9001"), "PRAC-50K", 50_000m, true, true, TradingMode.Practice)]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private PositionReconciliationService Service() =>
        new(Context(),
            _venueFactory,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = CredentialKey }),
            Options.Create(new FlattenOptions()),
            NullLogger<PositionReconciliationService>.Instance);

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
            CredentialKey = CredentialKey,
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
        });
        await seed.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task ReconcileAsync_ShouldBe404_WhenTheAccountIsNotFoundOrNotOwned()
    {
        // R-20 default-deny at the boundary: another operator's account is indistinguishable from one that does
        // not exist. Anything other than 404 here would disclose existence.
        IResult result = await PositionReconciliationEndpoints.ReconcileAsync(
            Guid.NewGuid(), instrument: null, Service(), CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldBe400_WhenTheInstrumentIsBlank()
    {
        // `?instrument=` scopes the read to one contract (gh#772); a blank symbol is a malformed request worth naming,
        // rejected before the venue is asked.
        IResult result = await PositionReconciliationEndpoints.ReconcileAsync(
            Guid.NewGuid(), "   ", Service(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .MustNotHaveHappened(); // rejected at the boundary -- the venue is never asked to resolve a blank symbol
    }

    [Fact]
    public async Task ReconcileAsync_ShouldBe400_WhenTheInstrumentResolvesToNoContract()
    {
        // gh#772 acceptance: the OTHER client mistake. A well-formed symbol the venue has no contract for parses fine,
        // reaches the venue, and the venue answers "no such contract" -- the endpoint names it 400, never a flat account
        // and never an Unknown-masquerade (which is reserved for an unreachable venue).
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new ProjectXVenueException("No ProjectX contract matches instrument 'ZZZZ'."));

        IResult result = await PositionReconciliationEndpoints.ReconcileAsync(
            accountId, "ZZZZ", Service(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void ReconciledPositionsResponse_ShouldCarryTheBasisAndEachPositionsQuantity()
    {
        ReconciledPositionsResponse response = new(
            PositionMarkBasis.Settlement.ToString(),
            [new ReconciledPosition("MESU26", 2, 5_300m, false)]);

        response.MarkBasis.Should().Be("Settlement");
        response.Positions[0].NetQuantity.Should().Be(2);
        response.Positions[0].IsFlat.Should().BeFalse();
    }
}
