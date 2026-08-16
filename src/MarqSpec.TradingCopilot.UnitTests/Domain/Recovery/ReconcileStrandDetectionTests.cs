using MarqSpec.TradingCopilot.Domain.Recovery;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Recovery;

/// <summary>
/// The pure runtime reconcile-strand decision (gh#722, ADR-0013): given the strands a sweep pass found, the
/// register's memory of past passes, the clock and an age bound, it decides which strands are <b>newly alertable</b>
/// and what the register should remember next — without transmitting, adopting, or releasing anything. The safety
/// properties: a fresh strand is not alerted until it has aged, an aged strand alerts <b>exactly once</b> while it
/// stays stranded, a resolved strand is forgotten, and a reappearance is treated as a fresh episode.
/// </summary>
public class ReconcileStrandDetectionTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly TimeSpan _bound = TimeSpan.FromMinutes(2);
    private readonly DateTimeOffset _t0 = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> _empty =
        new Dictionary<ReconcileStrandKey, ReconcileStrandEntry>();

    private ReconcileStrandKey OrderStrand(Guid id) => new(ReconcileStrandKind.OrderTaking, _owner, id);

    private ReconcileStrandKey ConditionalStrand(Guid id) => new(ReconcileStrandKind.ConditionalFiring, _owner, id);

    private ReconcileStrandDecision Detect(
        IReadOnlyList<ReconcileStrandKey> stranded,
        IReadOnlyDictionary<ReconcileStrandKey, ReconcileStrandEntry> register,
        DateTimeOffset now) =>
        ReconcileStrandDetection.Detect(stranded, register, now, _bound);

    [Fact]
    public void Detect_ShouldNotAlert_WhenAStrandIsFirstSeen()
    {
        // Age is measured from first observation, so a brand-new strand has age zero: it is remembered (first-seen
        // stamped now, not alerted) but never paged on the pass it first appears -- only a persisting one is stuck.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());

        ReconcileStrandDecision decision = Detect([strand], _empty, _t0);

        decision.NewlyAlertable.Should().BeEmpty();
        decision.Register.Should().ContainKey(strand);
        decision.Register[strand].FirstSeen.Should().Be(_t0);
        decision.Register[strand].Alerted.Should().BeFalse();
    }

    [Fact]
    public void Detect_ShouldAlertOnce_WhenAStrandHasAgedPastTheBound()
    {
        // Pass 1 clocks it; pass 2, past the bound, alerts it and records the alert in the register.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        ReconcileStrandDecision first = Detect([strand], _empty, _t0);

        ReconcileStrandDecision second = Detect([strand], first.Register, _t0 + _bound);

        second.NewlyAlertable.Should().ContainSingle().Which.Should().Be(strand);
        second.Register[strand].Alerted.Should().BeTrue();
        second.Register[strand].FirstSeen.Should().Be(_t0, "the first-seen instant is preserved across passes");
    }

    [Fact]
    public void Detect_ShouldAlert_WhenAgeExactlyEqualsTheBound()
    {
        // At-or-past the bound, not strictly past: a strand that has persisted exactly the bound is alertable.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        ReconcileStrandEntry firstSeen = new(_t0, Alerted: false);
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new() { [strand] = firstSeen };

        ReconcileStrandDecision decision = Detect([strand], register, _t0 + _bound);

        decision.NewlyAlertable.Should().ContainSingle().Which.Should().Be(strand);
    }

    [Fact]
    public void Detect_ShouldNotReAlert_WhenAStrandRemainsStrandedAcrossPasses()
    {
        // The idempotency guarantee: a maybe-live strand pages the operator ONCE, not on every pass. An already-
        // alerted strand that is still present must not re-alert. (Prove-red: ignore the Alerted flag and this fails.)
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        ReconcileStrandEntry alerted = new(_t0, Alerted: true);
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new() { [strand] = alerted };

        ReconcileStrandDecision decision = Detect([strand], register, _t0 + _bound + TimeSpan.FromMinutes(5));

        decision.NewlyAlertable.Should().BeEmpty("a continuously-stranded intent alerts exactly once");
        decision.Register[strand].Alerted.Should().BeTrue("the alerted flag is carried forward");
        decision.Register[strand].FirstSeen.Should().Be(_t0);
    }

    [Fact]
    public void Detect_ShouldForgetAStrand_WhenItIsNoLongerStranded()
    {
        // A strand that resolves (the operator reconciled it, or the take/fire completed) drops out of the register,
        // so nothing keeps paging about an intent that is no longer stuck.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        ReconcileStrandEntry alerted = new(_t0, Alerted: true);
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new() { [strand] = alerted };

        ReconcileStrandDecision decision = Detect([], register, _t0 + _bound);

        decision.NewlyAlertable.Should().BeEmpty();
        decision.Register.Should().NotContainKey(strand, "a resolved strand is forgotten");
    }

    [Fact]
    public void Detect_ShouldTreatAReappearanceAsFresh_WhenAResolvedStrandReturns()
    {
        // A resolved-then-returning id is a NEW episode: it is re-clocked from the reappearance and must age again
        // before it can alert -- so a flapping strand cannot re-page on every reappearance, and a genuinely re-
        // stranded intent still gets its own fresh look.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        ReconcileStrandDecision seen = Detect([strand], _empty, _t0);
        ReconcileStrandDecision aged = Detect([strand], seen.Register, _t0 + _bound); // alerts once
        aged.NewlyAlertable.Should().ContainSingle();

        ReconcileStrandDecision resolved = Detect([], aged.Register, _t0 + _bound + TimeSpan.FromMinutes(1));
        DateTimeOffset reappearsAt = _t0 + TimeSpan.FromMinutes(10);
        ReconcileStrandDecision reappears = Detect([strand], resolved.Register, reappearsAt);

        reappears.NewlyAlertable.Should().BeEmpty("the reappearance is re-clocked, not still-alerted");
        reappears.Register[strand].FirstSeen.Should().Be(reappearsAt, "first-seen resets on a fresh episode");
        reappears.Register[strand].Alerted.Should().BeFalse();

        // ...and it alerts AGAIN once the fresh episode itself ages past the bound.
        ReconcileStrandDecision reAged = Detect([strand], reappears.Register, reappearsAt + _bound);
        reAged.NewlyAlertable.Should().ContainSingle().Which.Should().Be(strand);
    }

    [Fact]
    public void Detect_ShouldAlertOnlyTheAgedStrands_WhenSeveralAreMixed()
    {
        // A pass can hold strands of different ages: only those past the bound (and not yet alerted) alert this pass.
        ReconcileStrandKey aged = OrderStrand(Guid.NewGuid());
        ReconcileStrandKey fresh = ConditionalStrand(Guid.NewGuid());
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new()
        {
            [aged] = new ReconcileStrandEntry(_t0, Alerted: false),
        };

        // `fresh` first appears now (age zero); `aged` was first seen at t0 and is now past the bound.
        ReconcileStrandDecision decision = Detect([aged, fresh], register, _t0 + _bound);

        decision.NewlyAlertable.Should().ContainSingle().Which.Should().Be(aged);
        decision.Register.Should().ContainKey(fresh);
        decision.Register[fresh].Alerted.Should().BeFalse();
    }

    [Fact]
    public void Detect_ShouldKeepOrderAndConditionalStrandsDistinct_WhenBothArePresent()
    {
        // The kind is part of the key, so an order and a conditional (however their ids relate) are independent
        // strands and each is tracked and alerted in its own right.
        ReconcileStrandKey order = OrderStrand(Guid.NewGuid());
        ReconcileStrandKey conditional = ConditionalStrand(Guid.NewGuid());
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new()
        {
            [order] = new ReconcileStrandEntry(_t0, Alerted: false),
            [conditional] = new ReconcileStrandEntry(_t0, Alerted: false),
        };

        ReconcileStrandDecision decision = Detect([order, conditional], register, _t0 + _bound);

        decision.NewlyAlertable.Should().BeEquivalentTo([order, conditional]);
    }

    [Fact]
    public void Detect_ShouldCountADuplicatedStrandOnce_WhenDiscoveryYieldsItTwice()
    {
        // Defensive dedupe: whatever the discovery hands us, one stranded id is one strand -- never a double page.
        ReconcileStrandKey strand = OrderStrand(Guid.NewGuid());
        Dictionary<ReconcileStrandKey, ReconcileStrandEntry> register = new()
        {
            [strand] = new ReconcileStrandEntry(_t0, Alerted: false),
        };

        ReconcileStrandDecision decision = Detect([strand, strand], register, _t0 + _bound);

        decision.NewlyAlertable.Should().ContainSingle().Which.Should().Be(strand);
    }

    [Fact]
    public void Detect_ShouldReturnAnEmptyDecision_WhenNothingIsStranded()
    {
        ReconcileStrandDecision decision = Detect([], _empty, _t0);

        decision.NewlyAlertable.Should().BeEmpty();
        decision.Register.Should().BeEmpty();
    }
}
