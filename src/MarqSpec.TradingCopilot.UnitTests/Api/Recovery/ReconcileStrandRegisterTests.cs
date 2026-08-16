using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Domain.Recovery;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The in-memory first-seen register behind the reconcile sweep (gh#722): it holds the pure decision's state between
/// passes — a snapshot to read, and a reconcile that swaps to the next state. The behaviours that matter: a fresh
/// register is empty, reconcile round-trips through snapshot, a strand absent from the next state is dropped
/// (resolved), and a first-seen instant carried forward survives.
/// </summary>
public class ReconcileStrandRegisterTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly DateTimeOffset _t0 = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    private ReconcileStrandKey Strand(Guid id) => new(ReconcileStrandKind.OrderTaking, _owner, id);

    [Fact]
    public void Snapshot_ShouldBeEmpty_WhenNothingHasBeenReconciled()
    {
        ReconcileStrandRegister register = new();

        register.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_ShouldRoundTripThroughSnapshot_WhenEntriesAreWritten()
    {
        ReconcileStrandRegister register = new();
        ReconcileStrandKey strand = Strand(Guid.NewGuid());
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> next = new()
        {
            [strand] = new ReconcileStrandEntry(_t0, Alerted: true),
        };

        register.Reconcile(next);

        IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> snapshot = register.Snapshot();
        snapshot.Should().ContainKey(strand);
        snapshot[strand].FirstSeen.Should().Be(_t0);
        snapshot[strand].Alerted.Should().BeTrue();
    }

    [Fact]
    public void Snapshot_ShouldBeDecoupledFromLaterReconciles_WhenTakenEarlier()
    {
        // The snapshot is a copy, so the pure decision reads a stable map even if the register is later swapped.
        ReconcileStrandRegister register = new();
        ReconcileStrandKey strand = Strand(Guid.NewGuid());
        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>
        {
            [strand] = new ReconcileStrandEntry(_t0, Alerted: false),
        });

        IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> snapshot = register.Snapshot();
        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>()); // resolve everything

        snapshot.Should().ContainKey(strand, "the earlier snapshot is a stable copy, not a live view");
    }

    [Fact]
    public void Reconcile_ShouldDropAStrand_WhenItIsAbsentFromTheNextState()
    {
        // A strand no longer present in the next state has resolved -- it must be forgotten, so nothing keeps paging.
        ReconcileStrandRegister register = new();
        ReconcileStrandKey resolved = Strand(Guid.NewGuid());
        ReconcileStrandKey surviving = Strand(Guid.NewGuid());
        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>
        {
            [resolved] = new ReconcileStrandEntry(_t0, Alerted: true),
            [surviving] = new ReconcileStrandEntry(_t0, Alerted: true),
        });

        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>
        {
            [surviving] = new ReconcileStrandEntry(_t0, Alerted: true),
        });

        IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> snapshot = register.Snapshot();
        snapshot.Should().ContainKey(surviving);
        snapshot.Should().NotContainKey(resolved);
    }

    [Fact]
    public void Reconcile_ShouldPreserveFirstSeen_WhenAnEntryIsCarriedForward()
    {
        // Across passes the decision carries a still-stranded entry forward with its original first-seen; the register
        // must store exactly that, so the strand's age keeps counting from when it was first observed.
        ReconcileStrandRegister register = new();
        ReconcileStrandKey strand = Strand(Guid.NewGuid());
        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>
        {
            [strand] = new ReconcileStrandEntry(_t0, Alerted: false),
        });

        // Next pass: same first-seen, now alerted.
        register.Reconcile(new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>
        {
            [strand] = new ReconcileStrandEntry(_t0, Alerted: true),
        });

        ReconcileStrandEntry entry = register.Snapshot()[strand];
        entry.FirstSeen.Should().Be(_t0, "the first-seen instant survives across reconciles");
        entry.Alerted.Should().BeTrue();
    }
}
