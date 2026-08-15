using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The in-memory register of flats awaiting their closing fill (gh#748). A <c>PositionEvent</c> (flat) and a
/// <c>FillEvent</c> (fill) are independent, unordered venue callbacks; when the flat is processed before the closing
/// fill is ingested it is parked here and retried once a fill for its account lands. Keyed on
/// <c>(Account, Contract.Key, At)</c> so distinct cycles on one contract coexist; first-seen preserved on a true
/// (same-<c>At</c>) re-delivery, filtered by account on read.
/// </summary>
public class PendingFlatJournalTests
{
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");

    private static PositionEvent Flat(string account, string contract = Contract) =>
        new(VenueAccountId.Create(Projectx, account),
            new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero),
            VenueContractId.Create(Projectx, contract),
            NetQuantity: 0,
            new Price(5_300m));

    [Fact]
    public void PendingFor_ShouldReturnTheRegisteredFlat_AfterRegister()
    {
        PendingFlatJournal journal = new();
        PositionEvent flat = Flat("9001");

        journal.Register(flat);

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001"))
            .Should().ContainSingle().Which.Should().BeSameAs(flat);
    }

    [Fact]
    public void PendingFor_ShouldNotReturnTheFlat_AfterResolve()
    {
        PendingFlatJournal journal = new();
        PositionEvent flat = Flat("9001");
        journal.Register(flat);

        journal.Resolve(flat);

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001")).Should().BeEmpty();
    }

    [Fact]
    public void PendingFor_ShouldReturnOnlyThatAccountsFlats_WhenSeveralAccountsHaveDeferrals()
    {
        PendingFlatJournal journal = new();
        PositionEvent nine = Flat("9001");
        PositionEvent eight = Flat("8002");
        journal.Register(nine);
        journal.Register(eight);

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001"))
            .Should().ContainSingle().Which.Should().BeSameAs(nine);
    }

    [Fact]
    public void PendingFor_ShouldKeepDistinctContracts_ForTheSameAccount()
    {
        // The key is (account, contract): two contracts flat on one account are two independent deferrals.
        PendingFlatJournal journal = new();
        PositionEvent mes = Flat("9001", "CON.F.US.MES.U26");
        PositionEvent nq = Flat("9001", "CON.F.US.NQ.U26");
        journal.Register(mes);
        journal.Register(nq);

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001")).Should().HaveCount(2);
    }

    [Fact]
    public void Register_ShouldCollapseToFirstSeen_WhenTheIdenticalFlatIsReDelivered()
    {
        // A reconnect replaying the IDENTICAL flat (same account, contract AND At) must not open a second deferral or
        // overwrite the exit terms we are waiting to close (gh#748 -- survives supervisor reconnects).
        PendingFlatJournal journal = new();
        PositionEvent first = Flat("9001");
        PositionEvent redelivered = Flat("9001"); // same account / contract / At -- the same event again
        journal.Register(first);
        journal.Register(redelivered);

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001"))
            .Should().ContainSingle().Which.Should().BeSameAs(first, "a true re-delivery does not overwrite the first-seen exit");
    }

    [Fact]
    public void Register_ShouldKeepBothCycles_WhenTwoDistinctFlatsRaceOnOneContract()
    {
        // gh#748 review: two round trips on the SAME contract can each be deferred at once, each awaiting its own
        // closing fill. The flat's own instant is part of the key, so the second (a distinct At) parks ALONGSIDE the
        // first rather than overwriting it -- overwriting would silently lose the second cycle's realized P&L, the
        // exact harm this feature prevents. (On the old (Account, Contract) key this returned one, dropping cycle two.)
        PendingFlatJournal journal = new();
        PositionEvent cycleOne = Flat("9001");
        PositionEvent cycleTwo = Flat("9001") with { At = cycleOne.At.AddMinutes(5) };
        journal.Register(cycleOne);
        journal.Register(cycleTwo);

        IReadOnlyCollection<PositionEvent> pending = journal.PendingFor(VenueAccountId.Create(Projectx, "9001"));
        pending.Should().HaveCount(2, "two distinct same-contract cycles are independent deferrals, not one slot");
        pending.Should().Contain(cycleOne).And.Contain(cycleTwo);
    }

    [Fact]
    public void PendingFor_ShouldBeEmpty_WhenNothingIsRegisteredForTheAccount()
    {
        PendingFlatJournal journal = new();
        journal.Register(Flat("8002"));

        journal.PendingFor(VenueAccountId.Create(Projectx, "9001")).Should().BeEmpty();
    }
}
