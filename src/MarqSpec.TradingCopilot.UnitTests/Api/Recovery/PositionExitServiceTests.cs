using FakeItEasy;
using FakeItEasy.Core;
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
    private readonly IPositionActionJournal _journal = A.Fake<IPositionActionJournal>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionExitServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(VenueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Returns(new ResolvedContract(VenueContractId.Create(Projectx, Contract), InstrumentId.Parse("MES")));
        Closes(Flat());
    }

    /// <summary>
    /// The single entry this exit journaled (gh#1143), read back off the seam rather than asked of the service.
    /// </summary>
    private PositionActionEntry Journaled()
    {
        ICompletedFakeObjectCall call = Fake.GetCalls(_journal)
            .Single(candidate => candidate.Method.Name == nameof(IPositionActionJournal.RecordAsync));
        return (PositionActionEntry)call.Arguments[0]!;
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
            _journal, NullLogger<PositionExitService>.Instance);

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
            _journal, NullLogger<PositionExitService>.Instance);

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

    // --- Every outcome leaves a record (gh#1143) ---

    [Fact]
    public async Task ExitAsync_ShouldJournalTheAttemptAndItsOutcome_WhenThePositionGoesFlat()
    {
        // Until gh#1143 this path transmitted a real order and left NO trace in the platform -- no journal entry,
        // no audit record, nothing. Every other order action (place, cancel, reprice, resize, withdraw, the
        // auto-flatten, the kill switch) records what it did; this one did not.
        Guid accountId = await SeedAccountAsync();

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        PositionActionEntry entry = Journaled();
        entry.Action.Should().Be(PositionActionKind.Exit);
        entry.Outcome.Should().Be(nameof(PositionExitOutcome.Flat));
        entry.OwnerUserId.Should().Be(_operator);
        entry.AccountId.Should().Be(accountId);
        entry.VenueAccountKey.Should().Be("9001");
        entry.Instrument.Should().Be("MES");
        entry.Contract.Should().Be(Contract);
        entry.NetQuantityAfter.Should().Be(0);
        // A full close asks for flat, not for a size.
        entry.RequestedQuantity.Should().BeNull();
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalTheExposureThatSurvived_WhenTheVenueStillReportsAPosition()
    {
        Guid accountId = await SeedAccountAsync();
        Closes(Position(2));

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        PositionActionEntry entry = Journaled();
        entry.Outcome.Should().Be(nameof(PositionExitOutcome.StillOpen));
        entry.NetQuantityAfter.Should().Be(2);
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalNoQuantityAtAll_WhenTheVenueCouldNotBeReached()
    {
        // The wire contract still answers 0 on Unreachable (a documented divergence from the reduce, #1142), but
        // the JOURNAL is what an incident reads back -- and a 0 there would manufacture a flat out of an outage,
        // exactly the gh#929 failure. The exposure is unknown, so it is recorded as unknown.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        PositionActionEntry entry = Journaled();
        entry.Outcome.Should().Be(nameof(PositionExitOutcome.Unreachable));
        entry.NetQuantityAfter.Should().BeNull();
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalTheAttempt_WhenTheVenueTimesOut()
    {
        // A venue timeout surfaces as an OperationCanceledException carrying HttpClient's OWN internal token, not
        // the caller's. It is a send fault -- durable intent, unknown outcome -- and it must leave a row.
        Guid accountId = await SeedAccountAsync();
        using CancellationTokenSource venueTimeout = new();
        await venueTimeout.CancelAsync();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .Throws(new TaskCanceledException("the venue send timed out", null, venueTimeout.Token));

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        Journaled().Outcome.Should().Be(nameof(PositionExitOutcome.Unreachable));
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalTheRefusal_WhenTheAccountBelongsToAnotherProcessCredentialSet()
    {
        // Nothing was sent, and that is itself worth a row: an incident asks what the operator tried, not only
        // what the venue did.
        Guid accountId = await SeedAccountAsync(credentialKey: "someone-else");

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        PositionActionEntry entry = Journaled();
        entry.Outcome.Should().Be(nameof(PositionExitOutcome.Unreachable));
        entry.Contract.Should().BeNull();
        entry.NetQuantityAfter.Should().BeNull();
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalNothing_WhenTheAccountIsNotTheCallers()
    {
        // A 404 for an account this operator does not own: there is no owner to stamp a row with, and recording
        // one would be a side channel telling the caller the id exists (R-20).
        Guid accountId = await SeedAccountAsync();

        await using TradingCopilotDbContext other = Context(Guid.NewGuid());
        PositionExitService service = new(
            other, _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            _journal, NullLogger<PositionExitService>.Instance);

        await service.ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        A.CallTo(() => _journal.RecordAsync(A<PositionActionEntry>._, A<DateTimeOffset>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExitAsync_ShouldStillReportTheVerifiedOutcome_WhenTheJournalFails()
    {
        // A record of a safety action must never be the reason the action reports something other than what
        // happened. The seam is contracted not to throw; this pins the caller's side of that even if it ever does.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _journal.RecordAsync(A<PositionActionEntry>._, A<DateTimeOffset>._))
            .Throws(new InvalidOperationException("the journal is down"));

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.Flat);
    }

    [Fact]
    public async Task ExitAsync_ShouldNotReportFlat_WhenTheCloseDidNotVerifyAndTheJournalFails()
    {
        // The other direction of the same rule: a failing journal must never launder an unverified close into a
        // success either.
        Guid accountId = await SeedAccountAsync();
        Closes(Position(2));
        A.CallTo(() => _journal.RecordAsync(A<PositionActionEntry>._, A<DateTimeOffset>._))
            .Throws(new InvalidOperationException("the journal is down"));

        PositionExitResult? result = await Service().ExitAsync(
            accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        result!.Outcome.Should().Be(PositionExitOutcome.StillOpen);
        result.NetQuantity.Should().Be(2);
    }

    [Fact]
    public async Task ExitAsync_ShouldJournalAfterTheVenueWasAsked_WhenItCloses()
    {
        // Transmit, then journal -- ADR-0007's accepted ordering for the send path (2026-08-02), applied unchanged
        // rather than re-answered here. The record carries the VERIFIED outcome, which cannot exist before the
        // attempt resolves; the residual crash window it leaves is named in the ADR update, not closed here.
        Guid accountId = await SeedAccountAsync();

        await Service().ExitAsync(accountId, InstrumentId.Parse("MES"), CancellationToken.None);

        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustHaveHappened()
            .Then(A.CallTo(() => _journal.RecordAsync(A<PositionActionEntry>._, A<DateTimeOffset>._))
                .MustHaveHappened());
    }
}
