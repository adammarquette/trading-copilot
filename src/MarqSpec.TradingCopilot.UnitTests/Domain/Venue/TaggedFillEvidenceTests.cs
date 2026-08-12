using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// The venue's answer to "did the order stamped this tag ever fill?" (gh#631), and — new in gh#770 — the
/// <b>fill legs</b> behind that answer.
/// </summary>
/// <remarks>
/// <para>
/// The legs exist so an adopt can backfill the entry <c>Fill</c> rows that were dropped while the row was
/// stranded without a venue key. They must carry the venue's <b>own</b> fill key, because <c>Fill</c>'s
/// uniqueness is <c>(OrderId, VenueFillKey)</c>: a backfill written under a synthesized key would not collide
/// with the same fill arriving later on the account-event stream, and the entry would be counted <b>twice</b> —
/// the mirror of the under-counting gh#770 exists to fix, and just as wrong for the R-5 governor.
/// </para>
/// <para>
/// Legs change nothing about the veto. This type remains <b>a veto, never an authorisation</b>: legs are
/// evidence to journal <i>after</i> a decision, never grounds to take one.
/// </para>
/// </remarks>
public class TaggedFillEvidenceTests
{
    private static readonly DateTimeOffset _at = new(2026, 8, 11, 14, 30, 0, TimeSpan.Zero);

    private static TaggedFillLeg Leg(string key, int size = 2) => new(key, 5_000m, size, 1.24m, _at);

    [Fact]
    public void Filled_ShouldCarryNoLegs_WhenTheVenueSuppliedNone()
    {
        // The no-regression case, and the reason legs are additive rather than required: a venue that can answer
        // "it filled" but cannot enumerate the fills still vetoes exactly as before. Empty, never null.
        TaggedFillEvidence evidence = TaggedFillEvidence.Filled("tag", 2m, 5_000m, "v1");

        evidence.Legs.Should().BeEmpty();
        evidence.VetoesRelease.Should().BeTrue();
    }

    [Fact]
    public void WithLegs_ShouldCarryTheVenuesOwnFillKeys_SoABackfillDedupesAgainstTheStream()
    {
        // The whole point of gh#770 option 2: these are the SAME keys the account-event stream would write, so
        // the { OrderId, VenueFillKey } unique index makes a later delivery or replay an idempotent no-op by
        // construction -- rather than a second row and a double-counted entry.
        TaggedFillEvidence evidence = TaggedFillEvidence.Filled("tag", 3m, 5_000m, "v1")
            .WithLegs([Leg("f-1", 2), Leg("f-2", 1)]);

        evidence.Legs.Select(leg => leg.VenueFillKey).Should().Equal("f-1", "f-2");
        evidence.Legs.Sum(leg => leg.Size).Should().Be(3);
    }

    [Fact]
    public void WithLegs_ShouldLeaveTheVetoUntouched_WhenLegsAreAttached()
    {
        // Legs are journalling evidence, not authority. Attaching them must not turn any other status into a
        // veto, or the type's central rule -- a veto, never an authorisation -- would be quietly reversed.
        TaggedFillEvidence unavailable = TaggedFillEvidence.Unavailable("tag").WithLegs([Leg("f-1")]);

        unavailable.VetoesRelease.Should().BeFalse();
        unavailable.Status.Should().Be(TaggedFillStatus.Unavailable);
    }

    [Fact]
    public void WithLegs_ShouldRefuseALegWithNoVenueKey_SoNoSynthesizedKeyCanReachTheJournal()
    {
        // The guard that makes the dedupe hold. A keyless leg is exactly the synthesized-key backfill this design
        // rejects, so it is refused at the boundary rather than written and discovered later as a duplicate.
        Action attach = () => TaggedFillEvidence.Filled("tag", 2m).WithLegs([new TaggedFillLeg(" ", 5_000m, 2, 0m, _at)]);

        attach.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithLegs_ShouldRefuseANonPositiveSize_SoAZeroLegCannotDiluteTheEntry()
    {
        Action attach = () => TaggedFillEvidence.Filled("tag", 2m).WithLegs([new TaggedFillLeg("f-1", 5_000m, 0, 0m, _at)]);

        attach.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsAmbiguous_ShouldStayTrue_WhenLegsAreAttachedToAMultiMatchEvidence()
    {
        // MatchCount > 1 means the evidence describes only the EARLIEST execution, so its legs are knowingly
        // partial. Attaching them must not read as completeness -- the caller still has to say so.
        TaggedFillEvidence evidence = TaggedFillEvidence
            .Filled("tag", 2m, 5_000m, "v1", matchCount: 2)
            .WithLegs([Leg("f-1")]);

        evidence.IsAmbiguous.Should().BeTrue();
    }
}
