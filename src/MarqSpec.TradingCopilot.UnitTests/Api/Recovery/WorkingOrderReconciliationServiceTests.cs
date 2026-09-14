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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The resting-orders reconcile (gh#381, R-1/R-13/R-17, ADR-0013): an account's <b>working orders — including the
/// attached protective bracket and its size</b> — read from venue truth.
/// </summary>
/// <remarks>
/// <para>
/// The app had a venue-truth read of <i>positions</i> and none of <i>resting orders</i>, so nothing could show
/// whether protection was actually standing at the exchange — the pre-live bracket gates had to reach around the
/// app to ProjectX directly to see it.
/// </para>
/// <para>
/// The failure modes guarded here are the ones that would make the read worse than not having it: an
/// <b>unreachable venue reported as an empty book</b> (which reads as "no resting orders" — the opposite of the
/// truth), and an account <b>this process holds no credentials for</b> being answered from anything other than
/// declared-unknown.
/// </para>
/// </remarks>
public class WorkingOrderReconciliationServiceTests
{
    private const string CredentialKey = "topstep-main";

    private static VenueId Venue => VenueId.Parse("projectx");

    private static DateTimeOffset Midday => new(2026, 7, 28, 17, 0, 0, TimeSpan.Zero); // 12:00 CT — a live session

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IProjectXVenueFactory _venueFactory = A.Fake<IProjectXVenueFactory>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public WorkingOrderReconciliationServiceTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venueFactory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Returns<IReadOnlyList<VenueAccount>>(
                [new VenueAccount(VenueAccountId.Create(Venue, "9001"), "PRAC-50K", 50_000m, true, true, TradingMode.Practice)]);
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private WorkingOrderReconciliationService Service(TradingCopilotDbContext context) =>
        new(context,
            _venueFactory,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = CredentialKey }),
            Options.Create(new FlattenOptions()),
            NullLogger<WorkingOrderReconciliationService>.Instance);

    private async Task<Guid> SeedAccountAsync(string credentialKey = CredentialKey)
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
        });
        await seed.SaveChangesAsync();
        return accountId;
    }

    private static WorkingOrder ProtectiveStop(int size) =>
        new("ORD-STOP", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), new Price(4_990m), null, size);

    [Fact]
    public async Task ReconcileAsync_ShouldReturnTheRestingProtectiveLeg_WithItsSize()
    {
        // The whole point of gh#381: a protective stop resting at the exchange, and HOW MUCH it covers.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([ProtectiveStop(2)]);
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Basis.Should().Be(PositionMarkBasis.Live);
        result.Orders.Should().ContainSingle();
        result.Orders[0].Size.Should().Be(2);
        result.Orders[0].StopPrice.Should().Be(new Price(4_990m));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldDeclareUnknown_WhenTheVenueCannotBeReached()
    {
        // The failure that would make this read WORSE than not having it. An unreachable venue returning an empty
        // list reads as "no resting orders" — the exact opposite of the truth, on a question about whether
        // protection is standing. Declared-unknown, with the orders withheld.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Basis.Should().Be(PositionMarkBasis.Unknown);
        result.Orders.Should().BeEmpty("an unknown basis must not carry a list a caller could read as the book");
    }

    [Fact]
    public async Task ReconcileAsync_ShouldDeclareUnknown_WhenThisProcessHoldsNoCredentialsForTheConnection()
    {
        // ADR-0015: another deployment's connection. We cannot see its venue truth, and must not imply we can.
        Guid accountId = await SeedAccountAsync(credentialKey: "someone-elses-key");
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown);
        result.Orders.Should().BeEmpty();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .MustNotHaveHappened(); // the venue must not even be asked for a connection this process cannot hold
    }

    [Fact]
    public async Task ReconcileAsync_ShouldDeclareUnknown_WhenTheVenueNoLongerReportsTheAccount()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Returns<IReadOnlyList<VenueAccount>>([]);
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown);
        result.Orders.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldBeNull_WhenTheAccountIsNotOwnedByTheCaller()
    {
        // R-20 default-deny: another operator's account is NOT FOUND, never "found but empty" — the endpoint maps
        // this to 404, so existence itself is not disclosed.
        await SeedAccountAsync();
        await using TradingCopilotDbContext other = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

        WorkingOrderReconciliation? result = await Service(other)
            .ReconcileAsync(await FirstAccountIdAsync(), Midday, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldReturnAnEmptyBook_WhenTheVenueGenuinelyHasNoRestingOrders()
    {
        // Distinct from unreachable, and the distinction is the value: a LIVE basis with no orders means the book
        // really is empty — which, with an open position, is the unprotected case worth acting on.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]);
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Live);
        result.Orders.Should().BeEmpty();
    }

    private static InstrumentId Es => InstrumentId.Parse("ES");

    private static VenueContractId EsContract => VenueContractId.Create(Venue, "CON.F.US.MES.U26");

    private static VenueContractId NqContract => VenueContractId.Create(Venue, "CON.F.US.MNQ.U26");

    private static WorkingOrder EsStop => new("ORD-ES", EsContract, new Price(4_990m), null, 2);

    private static WorkingOrder NqStop => new("ORD-NQ", NqContract, new Price(19_900m), null, 1);

    [Fact]
    public async Task ReconcileAsync_ShouldReturnOnlyTheInstrumentsOrders_WhenScopedToAnInstrument()
    {
        // gh#772: the chart overlays ONE instrument, so the scoped read filters to that instrument's contract — an
        // order resting on a different contract must not bleed onto its chart.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([EsStop, NqStop]);
        A.CallTo(() => _venue.ResolveContractAsync(Es, A<CancellationToken>._))
            .Returns(new ResolvedContract(EsContract, Es));
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Es, Midday, CancellationToken.None);

        result!.Orders.Should().ContainSingle();
        result.Orders[0].VenueOrderKey.Should().Be("ORD-ES");
    }

    [Fact]
    public async Task ReconcileAsync_ShouldReturnEveryContract_WhenNotScopedToAnInstrument()
    {
        // The unscoped overload — and the 3-arg one that delegates to it with null — is unchanged: no resolve, every
        // order. This is what the safety-critical callers (auto-flatten, the reconcile paths) keep getting.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([EsStop, NqStop]);
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Midday, CancellationToken.None);

        result!.Orders.Should().HaveCount(2);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldRaiseInstrumentNotResolvable_WhenTheVenueHasNoContractForTheInstrument()
    {
        // gh#772 acceptance: a well-formed symbol the venue has NO contract for is a CLIENT mistake, not unobtainable
        // truth. The read reached the venue (roster + book), so the resolve's not-found is the venue answering "no such
        // contract" -- it must surface as a 400 (this exception, mapped at the endpoint), never Unknown (reserved for an
        // unreachable venue) and never an empty "nothing resting on this instrument".
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([EsStop]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new ProjectXVenueException("No ProjectX contract matches instrument 'ES'."));
        await using TradingCopilotDbContext context = Context();

        Func<Task> read = () => Service(context).ReconcileAsync(accountId, Es, Midday, CancellationToken.None);

        (await read.Should().ThrowAsync<InstrumentNotResolvableException>())
            .Which.Instrument.Should().Be(Es);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldDeclareUnknown_WhenResolvingTheContractHitsATransportFault()
    {
        // A transport fault mid-resolve is NOT the venue's not-found type, so it is genuine unobtainable truth --
        // exactly like an unreachable book read: declared-unknown, never a spurious 400.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([EsStop]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable mid-resolve"));
        await using TradingCopilotDbContext context = Context();

        WorkingOrderReconciliation? result = await Service(context)
            .ReconcileAsync(accountId, Es, Midday, CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown);
        result.Orders.Should().BeEmpty();
    }

    private async Task<Guid> FirstAccountIdAsync()
    {
        await using TradingCopilotDbContext context = Context();
        return await context.Accounts.Select(account => account.Id).FirstAsync();
    }
}
