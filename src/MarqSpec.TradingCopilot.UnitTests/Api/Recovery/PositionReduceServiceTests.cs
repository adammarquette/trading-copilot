using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Orders;
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
/// The three properties these tests defend: it is <b>strictly partial</b> (a request at or beyond the open size is
/// refused before the venue is touched, so a reduce can never become a bracket-stranding flatten); <b>only a
/// verified reduction is success</b> (the outcome is read from venue truth after the attempt, against the exact
/// amount asked for); and <b>an exposure nobody could read is never reported as a number</b> (an unreachable venue
/// carries a null net quantity, not a fabricated flat).
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
    private IAccountEntryGuard _guard = null!;

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionReduceServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        RosterReports(TradingMode.Practice);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Returns(new ResolvedContract(ContractId, InstrumentId.Parse("MES")));
        Holds(3);      // long 3 by default
        ReducesTo(2);  // the venue takes one off by default
        _guard = PassthroughGuard();
    }

    /// <summary>
    /// The account is free, so the guard takes the lock and just invokes the callback. The real cross-request
    /// serialization is a Postgres advisory lock (gh#531) the EF in-memory provider cannot evaluate, so the unit
    /// tier proves what runs INSIDE the lock; the busy path has its own test with a busy fake.
    /// </summary>
    private static IAccountEntryGuard PassthroughGuard()
    {
        IAccountEntryGuard guard = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => guard.TryRunExclusiveAsync<PositionReduceResult?>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<PositionReduceResult?>>>._,
                A<Func<PositionReduceResult?>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<PositionReduceResult?>> transmit,
                Func<PositionReduceResult?> _, CancellationToken _) => transmit());
        return guard;
    }

    /// <summary>A guard whose account is already locked by another transmit: onBusy runs, the callback never does.</summary>
    private static IAccountEntryGuard BusyGuard()
    {
        IAccountEntryGuard guard = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => guard.TryRunExclusiveAsync<PositionReduceResult?>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<PositionReduceResult?>>>._,
                A<Func<PositionReduceResult?>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<PositionReduceResult?>> _,
                Func<PositionReduceResult?> onBusy, CancellationToken _) => Task.FromResult(onBusy()));
        return guard;
    }

    private static PositionSnapshot Position(int net) =>
        new(VenueAccount, ContractId, net, new Price(5_000m));

    private void RosterReports(TradingMode mode) =>
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(VenueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, mode)]);

    private void Holds(int net) =>
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(net == 0 ? [] : [Position(net)]);

    private void ReducesTo(int net) =>
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).Returns(Position(net));

    private void ReduceThrows(Exception error) =>
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).Throws(error);

    private void VenueMustNotHaveBeenAskedToReduce() =>
        A.CallTo(() => _venue.ReducePositionAsync(
            A<VenueAccountId>._, A<VenueContractId>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private PositionReduceService Service(string credentialKey = "topstep-main") =>
        new(Context(), _factory, _guard,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            NullLogger<PositionReduceService>.Instance);

    private async Task<Guid> SeedAccountAsync(
        string credentialKey = "topstep-main", TradingMode mode = TradingMode.Practice)
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
            Stage = mode == TradingMode.Practice ? AccountStage.Practice : AccountStage.Funded,
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
        });
        await seed.SaveChangesAsync();
        return accountId;
    }

    private Task<PositionReduceResult?> Reduce(Guid accountId, int quantity) =>
        Service().ReduceAsync(accountId, InstrumentId.Parse("MES"), quantity, CancellationToken.None);

    // --- Only a verified reduction is success ---

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
    public async Task ReduceAsync_ShouldReduceAShortTowardFlat_WhenTheVenueReportsExactlyTheRequestedSmallerShort()
    {
        // Symmetry: a short reduces toward flat the same way, and the exact-delta rule is measured on magnitude.
        Guid accountId = await SeedAccountAsync();
        Holds(-4);
        ReducesTo(-1);

        PositionReduceResult? result = await Reduce(accountId, 3);

        result!.Outcome.Should().Be(PositionReduceOutcome.Reduced);
        result.NetQuantity.Should().Be(-1);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnconfirmed_WhenTheVenueAcceptedButStillReportsTheOriginalSize()
    {
        // The card's central property: a partial close the venue ACCEPTED while still reporting the original size
        // is NOT a reduction. Reporting it done would tell the operator their exposure is smaller than it is.
        //
        // It is Unconfirmed rather than NotReduced, and the distinction is safety-critical: a market close is not
        // instantaneous, so an unchanged read-back means EITHER the fill has not settled OR nothing executed, and
        // the two are indistinguishable from here. Calling it "not reduced" invites a re-send, and the send is
        // non-idempotent -- a retry inside the settle window takes the size off twice, past flat into a reversal.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(3);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unconfirmed);
        result.Outcome.Should().NotBe(PositionReduceOutcome.Reduced);
        result.NetQuantity.Should().Be(3);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenTheVenueClosedLessThanRequested()
    {
        // Under-execution: asked to take 3 off a long 5 (expecting 2 left); the venue only closed 1, so 4 remain —
        // MORE exposure than the operator targeted. Not-done with the true net, never a green they have to catch.
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
        // Over-execution, short of flat: a protective stop or a concurrent exit took more off than asked. Smaller
        // is not the test — EXACTLY the requested delta is, so this is not-done on the true net.
        Guid accountId = await SeedAccountAsync();
        Holds(5);
        ReducesTo(1);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(1);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenThePartialCloseReachedFlat()
    {
        // The gh#1012 flat-via-partial-close race: a reduce-by-1 that came back FLAT closed more than asked (a stop
        // partial-filled underneath it). The operator meant to keep 2 on, and a bracket may now be dangling over a
        // flat position — so this must be visibly not-done, never a clean success.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(0);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(0);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenThePositionReversedSide()
    {
        // A side flip is a REVERSAL, not a reduction — however small its magnitude. long 3 coming back short 1 has
        // a smaller absolute size but it is new, opposite exposure.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(-1);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(-1);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportNotReduced_WhenTheFlipHappensToMatchTheRequestedMagnitude()
    {
        // The mutation this test exists to kill: dropping the side check and comparing magnitudes alone. long 3
        // reduced by 1 targets a magnitude of 2 — and a SHORT 2 has that magnitude while being a reversal into
        // fresh opposite exposure. Magnitude agreement is not a reduction.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReducesTo(-2);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.NotReduced);
        result.NetQuantity.Should().Be(-2);
    }

    // --- Strictly partial: refused before the venue is touched ---

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheQuantityEqualsWhatIsOpen()
    {
        // Reducing by the WHOLE position is a full close — which belongs to the exit path, because a full close
        // cancels the protective OCO legs (gh#183) and this sized partial close does not. Refused before the venue
        // is touched, so a reduce can never silently become a bracket-stranding flatten.
        Guid accountId = await SeedAccountAsync();
        Holds(2);

        PositionReduceResult? result = await Reduce(accountId, 2);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(2);
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheQuantityEqualsWhatIsOpenOnAShort()
    {
        // The short mirror: the guard is on MAGNITUDE, so a sign slip that made `quantity >= beforeQuantity` would
        // wave every short through (2 >= -2 is false) and let a reduce flatten it.
        Guid accountId = await SeedAccountAsync();
        Holds(-2);

        PositionReduceResult? result = await Reduce(accountId, 2);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(-2);
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheQuantityExceedsWhatIsOpen()
    {
        Guid accountId = await SeedAccountAsync();
        Holds(2);

        PositionReduceResult? result = await Reduce(accountId, 5);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(2);
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuseAndNotTouchTheVenue_WhenNothingIsOpen()
    {
        // Nothing to reduce is not a venue fault, and must not reach the venue: a reduce against a flat contract
        // would otherwise be a partial close the gateway rejects as "no position", mislabelled as unreachable.
        Guid accountId = await SeedAccountAsync();
        Holds(0);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(0);
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldSizeTheGuardOnTheRequestedContractOnly()
    {
        // The before-read is an account-wide list. Sizing the guard off another contract's position would let a
        // reduce-by-3 through against a long 1 — the flatten-and-strand case this guard exists to stop.
        Guid accountId = await SeedAccountAsync();
        VenueContractId otherContract = VenueContractId.Create(Projectx, "CON.F.US.ENQ.U26");
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(VenueAccount, otherContract, 10, new Price(20_000m)),
                Position(1),
            ]);

        PositionReduceResult? result = await Reduce(accountId, 3);

        result!.Outcome.Should().Be(PositionReduceOutcome.ExceedsPosition);
        result.NetQuantity.Should().Be(1);
        VenueMustNotHaveBeenAskedToReduce();
    }

    // --- A fault is never a reduction, and never a fabricated flat ---

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachableWithNoNetQuantity_WhenTheVenueReduceThrows()
    {
        // A venue fault is NOT "it reduced". And the net quantity stays UNKNOWN: after a failed partial close the
        // position may be unchanged, partly reduced, or flat — reporting 0 would manufacture a flat out of an
        // outage, the exposure-read failure gh#929 exists to prevent.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReduceThrows(new InvalidOperationException("venue unreachable"));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().BeNull();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenTheVenueSendTimesOut()
    {
        // A venue timeout surfaces as a TaskCanceledException carrying HttpClient's OWN internal token, not the
        // caller's. Catching cancellation broadly would let that escape as an aborted request; the operator would
        // see a dead connection instead of "the venue could not be reached, your position may be unchanged".
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReduceThrows(new TaskCanceledException("timeout", null, new CancellationTokenSource(0).Token));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().BeNull();
    }

    [Fact]
    public async Task ReduceAsync_ShouldPropagateCancellation_WhenTheCallerGoesAway()
    {
        // The mirror of the case above: the CALLER's own cancellation is an aborted request, not a business
        // outcome, so it must surface rather than be laundered into a reported "Unreachable".
        Guid accountId = await SeedAccountAsync();
        using CancellationTokenSource caller = new();
        await caller.CancelAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(caller.Token));

        Func<Task> act = async () => await Service()
            .ReduceAsync(accountId, InstrumentId.Parse("MES"), 1, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenReadingTheStartingPositionThrows()
    {
        // The before-read is on the same venue session; a fault reading it is just as unreachable, and must never
        // fall through to a reduce sized against a starting size nobody could confirm.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnreachable_WhenTheVenueNoLongerReportsTheAccount()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>([]);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
    }

    // --- Ownership and credential scoping ---

    [Fact]
    public async Task ReduceAsync_ShouldReturnNull_WhenTheAccountIsNotTheCallers()
    {
        // R-20: another operator's account is not found, not forbidden.
        Guid accountId = await SeedAccountAsync();

        await using TradingCopilotDbContext other = Context(Guid.NewGuid());
        PositionReduceService service = new(
            other, _factory, _guard,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            NullLogger<PositionReduceService>.Instance);

        (await service.ReduceAsync(accountId, InstrumentId.Parse("MES"), 1, CancellationToken.None)).Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldRefuse_WhenTheAccountBelongsToAnotherProcessCredentialSet()
    {
        // One credential set per process (ADR-0015): reducing on an account this process does not serve would be
        // acting through someone else's venue session.
        Guid accountId = await SeedAccountAsync(credentialKey: "someone-else");

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task ReduceAsync_ShouldThrow_WhenTheQuantityIsNotPositive(int quantity)
    {
        // A defensive guard: the endpoint rejects a non-positive quantity as a 400, but a mis-wired caller must
        // never reach the venue with a meaningless size.
        Guid accountId = await SeedAccountAsync();

        Func<Task> act = async () => await Reduce(accountId, quantity);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        VenueMustNotHaveBeenAskedToReduce();
    }

    // --- A definitive refusal is not an outage, and an indeterminate one is not a no-op ---

    [Fact]
    public async Task ReduceAsync_ShouldReportRefusedWithTheUnchangedSize_WhenTheVenueDefinitivelyRefused()
    {
        // The venue ANSWERED, in the negative, and executed nothing (gh#629). Folding that into Unreachable would
        // state two falsehoods at once -- that the venue was not reached, and that the exposure is unknowable. It
        // is knowable: it is exactly what it was before the attempt, and the operator gets that number.
        Guid accountId = await SeedAccountAsync();
        Holds(4);
        ReduceThrows(new VenueRefusalException("InvalidCloseSize", VenueRefusalKind.Definitive, 5));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Refused);
        result.Outcome.Should().NotBe(PositionReduceOutcome.Unreachable);
        result.NetQuantity.Should().Be(4);
    }

    [Fact]
    public async Task ReduceAsync_ShouldReportUnconfirmedWithNoNetQuantity_WhenTheRefusalIsIndeterminate()
    {
        // Indeterminate is gh#629's fail-safe default: the close MAY be live. It must never read as "nothing
        // happened" -- that is what would invite the re-send that takes the size off twice.
        Guid accountId = await SeedAccountAsync();
        Holds(4);
        ReduceThrows(new VenueRefusalException("OrderPending", VenueRefusalKind.Indeterminate, 7));

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Unconfirmed);
        result.NetQuantity.Should().BeNull();
    }

    // --- Serialization: a second non-idempotent close must never stack on one in flight ---

    [Fact]
    public async Task ReduceAsync_ShouldReportAccountBusyAndSendNothing_WhenAnotherTransmitHoldsTheAccountLock()
    {
        // The gh#531 hazard class, on a write where it is worse: two concurrent reduces both size against the same
        // pre-reduce snapshot, both pass the strict-partial guard, and both transmit -- taking off twice what was
        // asked. Refusing beats waiting, because a queued second close would still be sized against a stale read.
        Guid accountId = await SeedAccountAsync();
        _guard = BusyGuard();

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.AccountBusy);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
    }

    [Fact]
    public async Task ReduceAsync_ShouldTakeTheAccountLockBeforeReadingTheStartingPosition()
    {
        // The lock has to span the before-read AND the close, not just the close: the strict-partial guard is sized
        // off that read, so a lock taken after it would let two racers both read the same size and both proceed.
        Guid accountId = await SeedAccountAsync();
        _guard = BusyGuard();

        await Reduce(accountId, 1);

        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The two holds, made structural: practice-only is ENFORCED, not asserted ---

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Undeclared)]
    public async Task ReduceAsync_ShouldHoldAndSendNothing_WhenTheAccountIsNotAPracticeAccount(TradingMode mode)
    {
        // The reduce ships behind two named holds -- the never-run gh#1012 bracket verification, and the
        // client-side auto-retry of this non-idempotent write (MarqSpec.Client.ProjectX#98). Both say "not on a
        // funded account", and in prose alone that is a promise the next session can break without noticing. This
        // is the promise the compiler keeps. Nothing reaches the venue -- not even the position read.
        Guid accountId = await SeedAccountAsync(mode: mode);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.HeldPracticeOnly);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldHoldAndSendNothing_WhenTheVenueReportsANonPracticeModeForAPracticeRow()
    {
        // Defence in depth, and the case a single check cannot cover: the persisted Account.Mode is a DERIVATION
        // that can lag a firm-conventions change, so a stale row saying "practice" must not wave through an
        // account the venue's own roster resolves as funded. Checked inside the lock, off the roster read that
        // already happens.
        Guid accountId = await SeedAccountAsync(); // the row still says Practice
        RosterReports(TradingMode.Live);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.HeldPracticeOnly);
        result.NetQuantity.Should().BeNull();
        VenueMustNotHaveBeenAskedToReduce();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReduceAsync_ShouldProceed_WhenBothTheRowAndTheRosterSayPractice()
    {
        // The paired assertion that keeps the guard honest: it must refuse a funded account WITHOUT refusing a
        // practice one, or the whole path would be dead and every other test here would pass vacuously.
        Guid accountId = await SeedAccountAsync();
        RosterReports(TradingMode.Practice);

        PositionReduceResult? result = await Reduce(accountId, 1);

        result!.Outcome.Should().Be(PositionReduceOutcome.Reduced);
    }

    // --- A venue that cannot do this at all refuses loudly, rather than looking like an outage ---

    [Fact]
    public async Task ReduceAsync_ShouldPropagate_WhenTheVenueCannotSizeAPartialClose()
    {
        // The seam's fail-loud default (R-17) throws NotSupportedException. Laundering it into Unreachable would
        // tell the operator the venue was down when the truth is that this venue cannot size a partial close at
        // all -- an operator who retries an outage forever never learns the control does not exist here.
        Guid accountId = await SeedAccountAsync();
        Holds(3);
        ReduceThrows(new NotSupportedException("this venue cannot size a partial close"));

        Func<Task> act = async () => await Reduce(accountId, 1);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
