using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for gh#759's <b>FIFO per-leg trade pairing</b> and the composite natural key
/// <c>(ClosingFillId, OpeningFillId)</c> it moved to (gh#799; ADR-0022; R-8, R-9, and the R-4 / R-5 governors the
/// <c>Trade</c> rows feed). Authored from gh#799's acceptance list and gh#759's statement of the hazard rather than
/// from the writer — this tier's independence is the point of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this tier.</b> The unit tier owns the FIFO composition and the in-memory idempotency pre-check, both
/// shipped with #759. Everything asserted here is invisible to it: a <b>filtered composite UNIQUE index</b> (EF
/// InMemory enforces no index at all), a real <c>23505</c> carrying that index's own <c>ConstraintName</c> (which
/// only Npgsql raises, and which the in-memory gate can never produce), and <c>DailyRealizedReader</c>'s
/// Central-day bucketing over per-leg <c>ClosedAt</c> values. Every case runs the shipped migrations against
/// <c>timescale/timescaledb-ha:pg17</c> through <see cref="TradeFifoPairingPostgresFactory"/>.
/// </para>
/// <para>
/// <b>The migration half lives next door.</b> gh#799's remaining two bullets — the migration applied over
/// populated pre-#759 rows, and the <c>Down</c> hazard once a spanning-exit row exists — need a database standing
/// <i>before</i> the migration, which this fixture's (already at head) cannot be. They are
/// <c>Data/TradeOpeningFillKeyMigrationIntegrationTests</c>, which owns its own container per case, the shape
/// <c>TriggerConfirmationBackfillIntegrationTests</c> established.
/// </para>
/// <para>
/// <b>The hazard being guarded.</b> Before #759 a scale-in-with-partial-exit or a stop-and-reverse was <i>refused</i>
/// — no <c>Trade</c> row — so its realized loss never reached the daily governor and the operator kept headroom
/// they had already spent. #759 journals those cycles per leg, which is only safe if three things hold on real
/// Postgres at once: the split is <b>accepted</b> (two legs may share one closing fill), a genuine duplicate is
/// still <b>rejected</b>, and the split's per-leg P&amp;L <b>sums to the same aggregate</b> the governor would have
/// read from an equivalent balanced trip. Those are the first three sections below; the rest guard the ways the new
/// key can go wrong — a re-pairing double-count, a sign flip on a tie.
/// </para>
/// <para>
/// <b>Base.</b> This branch is cut from <c>feat/759_journal-pairing-policy</c> (PR #800), not from <c>develop</c>:
/// <c>Trade.OpeningFillId</c> and the <c>AddTradeOpeningFillKey</c> migration exist only there, so a suite based on
/// <c>develop</c> would fail to <i>compile</i> rather than report a behaviour difference — and a QA project that
/// does not compile takes every other suite down with it. It must not merge before #800 does.
/// </para>
/// </remarks>
public class TradeFifoPairingIntegrationTests : IClassFixture<TradeFifoPairingPostgresFactory>
{
    /// <summary>The composite unique index the natural key is enforced by.</summary>
    private const string CompositeIndex = "IX_Trades_ClosingFillId_OpeningFillId";

    private const string Contract = "CON.F.US.MES.U26";
    private const string OurCredentialKey = "topstep-main";
    private const decimal ContractPointValue = 5m;

    /// <summary>The window every direct-call case composes in — fixed, so nothing depends on when CI runs.</summary>
    private static readonly DateTimeOffset _origin = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The instant the governor cases read "today" against — the Central day <c>_origin</c> sits in.</summary>
    private static readonly DateTimeOffset _readAt = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    /// <summary>An earlier Central day, for the historical rows that keep the consistency window non-trivial.</summary>
    private static readonly DateTimeOffset _priorDay = new(2026, 8, 10, 15, 0, 0, TimeSpan.Zero);

    private static readonly VenueId _projectx = VenueId.Parse("projectx");

    private readonly TradeFifoPairingPostgresFactory _factory;

    public TradeFifoPairingIntegrationTests(TradeFifoPairingPostgresFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // 1. The composite UNIQUE index enforces (gh#799 bullet 1) — the split is accepted, a genuine duplicate is not.
    // =================================================================================================================

    [Fact]
    public async Task Persistence_ShouldAcceptBothLegsOfASpanningExit_WhenTheyShareAClosingFillButNotAnOpeningFill()
    {
        // The shape gh#759 made legal and the OLD single-column IX_Trades_ClosingFillId made impossible: one exit
        // fill retires two scale-in legs, so two Trade rows carry the SAME ClosingFillId. Asserted at the raw
        // context, independent of the writer, because it is the INDEX's contract under test here.
        //
        // PROVE-RED: this case cannot be reddened by editing the writer — the schema decides it. It was reddened by
        // running the same two inserts against the PRE-#759 schema (the database the sibling migration suite stands
        // up at the preceding revision): the second insert fails 23505 on IX_Trades_ClosingFillId, which is exactly
        // the refusal #759 exists to remove.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7101");

        Guid openingA = FillId(1, 0x01);
        Guid openingB = FillId(1, 0x02);
        Guid spanningExit = FillId(1, 0x03);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-a-7101", [(openingA, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-b-7101", [(openingB, 4_990m, 1, At(1))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-7101", [(spanningExit, 4_980m, 2, At(2))]);

        await ExecuteDbAsync(async database =>
        {
            database.Trades.Add(Leg(owner, accountId, spanningExit, openingA, -100m));
            await database.SaveChangesAsync();
        });

        Func<Task> secondLeg = () => ExecuteDbAsync(async database =>
        {
            database.Trades.Add(Leg(owner, accountId, spanningExit, openingB, -50m));
            await database.SaveChangesAsync();
        });

        await secondLeg.Should().NotThrowAsync(
            "a spanning exit retires TWO legs and shares one closing fill — under the old single-column unique "
            + "index the second leg was rejected, which is why a scaled-in trade's realized loss never reached the "
            + "R-4 / R-5 daily governor (gh#759, ADR-0022)");

        List<Trade> legs = await TradesAsync(accountId);

        legs.Should().HaveCount(2, "both legs of the split are real rows, not one blended row");
        legs.Should().OnlyContain(
            leg => leg.ClosingFillId == spanningExit, "the one exit fill is what retired both legs");
        legs.Select(leg => leg.OpeningFillId).Should().BeEquivalentTo(
            new Guid?[] { openingA, openingB },
            "the opening fill is the half of the key that separates the legs — two legs with the same opening fill "
            + "would be a genuine duplicate, not a split");
    }

    [Fact]
    public async Task Persistence_ShouldRejectAGenuineDuplicateLeg_WithAUniqueViolationNamingTheCompositeIndex()
    {
        // The other half of the key's contract, and the one IsTradeNaturalKeyViolation is written against: the SAME
        // (ClosingFillId, OpeningFillId) pair twice is a replay, not a split, and the database itself must refuse
        // it. The exception TYPE alone would not prove that -- an FK or check failure is also a DbUpdateException --
        // so the refusal is asserted BY CONSTRAINT NAME, the discipline TradeJournalWriteFaultIntegrationTests
        // adopted after one of its cases passed on the wrong exception.
        //
        // PROVE-RED: drop IsUnique from the composite index (or widen its filter to one column) and the second
        // insert succeeds -- this case then fails on the missing throw.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7102");

        Guid opening = FillId(2, 0x01);
        Guid closing = FillId(2, 0x02);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-7102", [(opening, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-7102", [(closing, 5_010m, 1, At(2))]);

        await ExecuteDbAsync(async database =>
        {
            database.Trades.Add(Leg(owner, accountId, closing, opening, 50m));
            await database.SaveChangesAsync();
        });

        Func<Task> duplicate = () => ExecuteDbAsync(async database =>
        {
            database.Trades.Add(Leg(owner, accountId, closing, opening, 50m));
            await database.SaveChangesAsync();
        });

        await duplicate.Should().ThrowAsync<DbUpdateException>(
            "the same leg written twice is a double-count straight into the daily governor — the composite unique "
            + "index is the backstop the in-memory pre-check cannot be")
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(
                error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == CompositeIndex
                        || error.MessageText.Contains(CompositeIndex, StringComparison.Ordinal)),
                "IsTradeNaturalKeyViolation keys on THIS index name: a refusal raised by anything else would leave "
                + "the writer's catch matching nothing, and every ordinary replay would surface as a hard fault");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordDuplicateRejected_WhenTheOwnerScopedPreCheckMissesAndOnlyTheIndexCatches()
    {
        // The writer's backstop, end to end. Its in-memory pre-check runs FIRST and would hide the index, so this
        // exploits R-20 exactly as TradeJournalWriteFaultIntegrationTests does: a phantom Trade under a DIFFERENT
        // UserId carrying the same (ClosingFillId, OpeningFillId). The owner-scoped pre-check genuinely finds
        // nothing; the unique index is not owner-filtered, so SaveChanges still collides -- deterministically,
        // with no race to arrange.
        //
        // PROVE-RED: point IsTradeNaturalKeyViolation at the OLD single-column name and the real 23505 stops
        // matching the writer's `when` guard -- the flat throws instead of recording duplicate-rejected, and this
        // case fails on the unhandled DbUpdateException rather than on the metric.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7103");

        Guid opening = FillId(3, 0x01);
        Guid closing = FillId(3, 0x02);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-7103", [(opening, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-7103", [(closing, 4_980m, 1, At(2))]);

        await ExecuteDbAsync(async database =>
        {
            database.Trades.Add(Leg(Guid.NewGuid(), accountId, closing, opening, -100m));
            await database.SaveChangesAsync();
        });

        _factory.Capture.Clear();

        bool journalled = await JournalAsync(FlatAt("7103", At(2)));

        journalled.Should().BeFalse(
            "the leg's natural key is already taken — the writer must skip, never write a second row for it");

        int rowsForTheLeg = await QueryDbAsync(database => database.Trades
            .IgnoreQueryFilters()
            .CountAsync(trade => trade.ClosingFillId == closing && trade.OpeningFillId == opening));
        rowsForTheLeg.Should().Be(
            1, "only the pre-seeded phantom survives — the index rejected the writer's insert, so the day's "
            + "realized P&L was not counted twice");

        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().ContainSingle(
            measurement =>
                (string?)measurement.Tags.GetValueOrDefault("outcome") == ExecutionMetrics.JournalDuplicateRejected,
            "a real 23505 on the COMPOSITE index must still be recognised as the benign duplicate it is — the "
            + "in-memory gate cannot raise one, so this is the only tier where that catch is actually exercised");
    }

    // =================================================================================================================
    // 2. Idempotent replay under the new key (gh#799 bullet 2) — every leg deduped, for both multi-leg shapes.
    // =================================================================================================================

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNoNewRows_WhenAScaleInFlatIsReplayed()
    {
        // At-least-once redelivery over a TWO-leg cycle. Under the old key this shape produced no row at all
        // (refused), so "a replay writes nothing new" was vacuously true; now there are two rows to dedupe and both
        // the per-leg pre-check and the composite index must dedupe BOTH. One leg deduped and one re-written would
        // silently inflate the day by half a trade.
        //
        // PROVE-RED: the two halves were reddened separately -- removing the per-leg pre-check leaves the index
        // catching the replay (so the row count holds, and only the outcome tag moves), while dropping the index's
        // uniqueness as well lets the replay write two more rows and this case fails on the count.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7104");

        await SeedScaleInWithSpanningExitAsync(owner, accountId, "7104", block: 4);

        await JournalAsync(FlatAt("7104", At(2)));
        List<Trade> afterFirst = await TradesAsync(accountId);
        afterFirst.Should().HaveCount(2, "the scale-in journals one row per opening leg — the gh#759 change itself");

        await JournalAsync(FlatAt("7104", At(2)));

        List<Trade> afterReplay = await TradesAsync(accountId);
        afterReplay.Should().HaveCount(
            2, "a replayed flat recomposes the SAME legs, whose keys are the same rows — a third row is a "
            + "double-count straight into the R-4 / R-5 daily governor");
        afterReplay.Select(trade => trade.Id).Should().BeEquivalentTo(
            afterFirst.Select(trade => trade.Id),
            "the surviving rows must be the ORIGINAL ones — a replay that deleted and rewrote them would keep the "
            + "count right while changing the journal underneath the operator");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNoNewRows_WhenAStopAndReverseFlatIsReplayed()
    {
        // The other multi-leg shape, and the harder one: the reversing fill is BOTH the long leg's close and the
        // short leg's open, so it appears in two different keys -- (reverse, entry) and (cover, reverse). A dedup
        // that asked "have I seen this fill?" rather than "have I seen this PAIR?" would drop the second leg on the
        // first pass, and could pair it differently on the replay.
        //
        // PROVE-RED: as above; additionally, keying the dedup on ClosingFillId alone reddens the leg-identity
        // assertions here, because the two legs do not share a closing fill but the reversing fill appears twice.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7105");

        Guid entry = FillId(5, 0x01);
        Guid reverse = FillId(5, 0x02);
        Guid cover = FillId(5, 0x03);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-7105", [(entry, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "reverse-7105", [(reverse, 4_990m, 2, At(1))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "cover-7105", [(cover, 4_980m, 1, At(2))]);

        await JournalAsync(FlatAt("7105", At(2)));
        List<Trade> afterFirst = await TradesAsync(accountId);

        afterFirst.Should().HaveCount(
            2, "a stop-and-reverse is a long leg and a short leg — refusing it (the pre-#759 behaviour) kept both "
            + "results out of the governor entirely");
        afterFirst.Select(trade => trade.Side).Should().BeEquivalentTo(
            new[] { OrderSide.Buy, OrderSide.Sell },
            "the reversing fill closes the long and opens the short, so the legs sit on OPPOSITE sides — the same "
            + "side twice would mean a leg's sign was flipped on its way into the governor");
        afterFirst.Should().Contain(
            trade => trade.ClosingFillId == reverse && trade.OpeningFillId == entry,
            "the long leg is keyed (reversing fill, entry fill)");
        afterFirst.Should().Contain(
            trade => trade.ClosingFillId == cover && trade.OpeningFillId == reverse,
            "the short leg OPENS on the very fill that closed the long one — that shared fill is why the key had to "
            + "become a pair in the first place");

        await JournalAsync(FlatAt("7105", At(2)));

        (await TradesAsync(accountId)).Select(trade => trade.Id).Should().BeEquivalentTo(
            afterFirst.Select(trade => trade.Id),
            "replaying the flat must add nothing and change nothing — both legs dedupe on their own key");
    }

    // =================================================================================================================
    // 3. Governor equality across the split (gh#799 bullet 3) — the safety point of the whole card.
    // =================================================================================================================

    [Fact]
    public async Task TheGovernorReaders_ShouldReadTheSameAggregate_ForASplitScaleIn_AsForAnEquivalentBalancedTrip()
    {
        // Two accounts under one operator, constructed to be the SAME trade told two ways:
        //
        //   split     buy 1 @ 5000, buy 1 @ 4990, sell 2 @ 4980  ->  two legs: -100 and -50
        //   balanced  buy 2 @ 4995,               sell 2 @ 4980  ->  one trip:        -150
        //
        // Same size, same average entry, same exit, same point value, no fees -- so the SPLIT is the only variable
        // left, and any difference the readers report is it. This is the hazard gh#759 closes, stated as an
        // equality: before it the split side journalled nothing at all and the governor read 0 against a day that
        // had really lost 150.
        //
        // PROVE-RED: reachable BY BASE as well as by edit -- run this case against develop-before-#759 (with the
        // OpeningFillId assertions removed so it compiles) and the split account has no rows, so the daily read
        // comes back 0 against the balanced account's -150.
        Guid owner = Guid.NewGuid();
        Guid splitAccount = await SeedAccountAsync(owner, "7106");
        Guid balancedAccount = await SeedAccountAsync(owner, "7107");

        await SeedScaleInWithSpanningExitAsync(owner, splitAccount, "7106", block: 6);

        Guid balancedEntry = FillId(7, 0x01);
        Guid balancedExit = FillId(7, 0x02);
        await SeedOrderAsync(owner, balancedAccount, OrderSide.Buy, "entry-7107", [(balancedEntry, 4_995m, 2, At(0))]);
        await SeedOrderAsync(owner, balancedAccount, OrderSide.Sell, "exit-7107", [(balancedExit, 4_980m, 2, At(2))]);

        // An identical earlier profitable day on BOTH accounts, written straight at the context with no natural key
        // (the shape of a pre-#759 historical row). It is here so the consistency window is non-trivial: two empty
        // windows would compare equal without proving anything at all.
        await SeedHistoricalTradeAsync(owner, splitAccount, _priorDay, 200m);
        await SeedHistoricalTradeAsync(owner, balancedAccount, _priorDay, 200m);

        await JournalAsync(FlatAt("7106", At(2)));
        await JournalAsync(FlatAt("7107", At(2)));

        List<Trade> splitLegs = (await TradesAsync(splitAccount))
            .Where(trade => trade.ClosingFillId != null)
            .ToList();
        List<Trade> balancedTrips = (await TradesAsync(balancedAccount))
            .Where(trade => trade.ClosingFillId != null)
            .ToList();

        splitLegs.Should().HaveCount(2, "the scale-in is two legs under FIFO");
        balancedTrips.Should().ContainSingle("the balanced trip is one leg");

        splitLegs.Select(leg => leg.RealizedPnL).Should().BeEquivalentTo(
            new decimal?[] { -100m, -50m },
            "FIFO retires the OLDEST lot first: the 5000 lot loses 20 points and the 4990 lot 10, at a point value "
            + "of 5 — legs carrying the blended average would misreport both");

        balancedTrips[0].RealizedPnL.Should().Be(
            splitLegs.Sum(leg => leg.RealizedPnL ?? 0m),
            "the per-leg P&L must SUM to the single-trip total — the split is a finer decomposition of the same "
            + "money, never a different amount of it");

        decimal splitRealized = await TodayRealizedAsync(owner, splitAccount);
        decimal balancedRealized = await TodayRealizedAsync(owner, balancedAccount);

        splitRealized.Should().Be(
            -150m,
            "the realized LOSS of a scaled-in trade must reach the daily governor — reading 0 here is the exact "
            + "hazard gh#759 closes: headroom the operator has already spent, still showing as available");
        splitRealized.Should().Be(balancedRealized, "the governor cannot care how the same day was decomposed");

        ConsistencyWindow splitWindow = await ConsistencyWindowAsync(owner, splitAccount);
        ConsistencyWindow balancedWindow = await ConsistencyWindowAsync(owner, balancedAccount);

        splitWindow.Should().NotBe(
            ConsistencyWindow.Empty,
            "the seeded prior profit keeps the window non-trivial, so the equality below cannot pass by both sides "
            + "being empty");
        splitWindow.Should().Be(
            balancedWindow,
            "R-9's consistency window reads the same Trade rows — a split that moved the best-day or cumulative "
            + "figure would swing a payout evaluation on nothing but how the exit happened to fill");
    }

    [Fact]
    public async Task DailyRealizedReader_ShouldAttributeEachLegToTheCentralDayItClosedOn_WhenACycleStraddlesTheBoundary()
    {
        // gh#799's "notes / seams": per-leg rows carry each leg's OWN ClosedAt (the retiring fill's time) instead of
        // the old single max(ClosedAt). A cycle opened before Central midnight and finished after it therefore
        // splits ACROSS two trading days -- which is more accurate, and is only true if DailyRealizedReader's
        // Central-day bucketing really reads the per-leg time. Under a max(ClosedAt) attribution both legs land on
        // the later day: day one under-reports a loss it actually took (headroom that was really spent) and day two
        // over-reports one it did not (a governor that trips early).
        //
        // Fixed dates rather than "now": the reader takes `now` as a parameter, so the boundary is pinned instead of
        // depending on when CI runs. Central midnight is computed through MarketClock, so this holds either side of
        // a daylight-saving change.
        //
        // PROVE-RED: attribute every leg the cycle's last ClosedAt and the earlier day reads 0 while the later day
        // reads -150.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7108");

        DateTimeOffset centralMidnight = CentralMidnightUtc(2026, 8, 11);
        DateTimeOffset beforeMidnight = centralMidnight.AddMinutes(-10);
        DateTimeOffset afterMidnight = centralMidnight.AddMinutes(10);

        Guid firstLot = FillId(8, 0x01);
        Guid secondLot = FillId(8, 0x02);
        Guid firstExit = FillId(8, 0x03);
        Guid secondExit = FillId(8, 0x04);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-a-7108",
            [(firstLot, 5_000m, 1, centralMidnight.AddHours(-1))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-b-7108",
            [(secondLot, 4_990m, 1, centralMidnight.AddMinutes(-50))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-a-7108", [(firstExit, 4_980m, 1, beforeMidnight)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-b-7108", [(secondExit, 4_980m, 1, afterMidnight)]);

        await JournalAsync(FlatAt("7108", afterMidnight));

        List<Trade> legs = await TradesAsync(accountId);
        legs.Should().HaveCount(2, "two lots retired one at a time are two legs");
        legs.Select(leg => leg.ClosedAt).Should().BeEquivalentTo(
            new DateTimeOffset?[] { beforeMidnight, afterMidnight },
            "each leg closes when ITS retiring fill executed — a shared max(ClosedAt) puts both on the later day, "
            + "which is the attribution gh#759 replaced");

        decimal earlierDay = await TodayRealizedAsync(owner, accountId, centralMidnight.AddMinutes(-5));
        decimal laterDay = await TodayRealizedAsync(owner, accountId, centralMidnight.AddHours(7));

        earlierDay.Should().Be(
            -100m,
            "the 5000 lot was retired before Central midnight — its 20-point loss belongs to THAT trading day's "
            + "governor, not to the next one");
        laterDay.Should().Be(
            -50m,
            "only the lot retired after midnight belongs to the new day — attributing the whole 150 here would trip "
            + "a governor on a loss the day never took");
    }

    // =================================================================================================================
    // 4. The re-pairing fail-closed guard (gh#799 bullet 6; the gh#759 review's BLOCKING finding).
    // =================================================================================================================

    [Fact]
    public async Task ProcessFlatAsync_ShouldRefuseAndKeepTheUnderReportedRow_WhenLateFillsRePairTheJournalledLegs()
    {
        // A flat is processed before every fill has arrived, so the writer journals what it can see: one leg,
        // (exit @ t3, entry @ t0). Two executions then land LATE and OUT OF ORDER, both timed BEFORE that journalled
        // close. FIFO now re-pairs the very same fills into different legs -- (t2 exit, t0 entry) and (t3 exit, t2
        // entry) -- neither of which is the key already on disk. The exact-pair dedup cannot recognise them, so
        // writing them would ADD their P&L on top of the row already counted: a double-count into the daily
        // governor, in the dangerous direction. The writer must refuse and keep the one under-reported row.
        //
        // The late fills arrive through TestAccountEventStream and a real AccountEventStreamHost -- venue-neutral
        // events into the shipped ingestion service -- because the hazard is about fill DELIVERY, and hand-writing
        // the Fill rows would assume away the thing under test (gh#799 names this explicitly).
        //
        // PROVE-RED: remove the pre-write merge guard and the replay writes two more rows; the day then reports
        // roughly three times the loss it actually took.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "7111");

        Guid entry = FillId(11, 0x01);
        Guid journalledExit = FillId(11, 0x04);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "entry-7111", [(entry, 5_000m, 1, At(0))]);
        // The two orders whose executions the venue has not reported yet: seeded WITHOUT fills, so ingestion has a
        // VenueOrderKey to resolve when the late ones finally arrive.
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, "late-entry-7111", []);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "late-exit-7111", []);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, "exit-7111", [(journalledExit, 4_980m, 1, At(3))]);

        await JournalAsync(FlatAt("7111", At(3)));

        List<Trade> journalled = await TradesAsync(accountId);
        journalled.Should().ContainSingle("the partial view of the cycle composes exactly one leg — the precondition");
        journalled[0].ClosingFillId.Should().Be(journalledExit, "and it is keyed on the only exit fill yet seen");
        journalled[0].OpeningFillId.Should().Be(entry, "paired with the only entry fill yet seen");
        Guid survivingRowId = journalled[0].Id;
        decimal? survivingPnL = journalled[0].RealizedPnL;

        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>().MakeAccountStreamingSupported();
        TestAccountEventStream stream = _factory.Services.GetRequiredService<TestAccountEventStream>();
        stream.Reset();
        _factory.Capture.Clear();

        AccountEventStreamHost host = new(
            _factory.Services, _factory.Services.GetRequiredService<ILogger<AccountEventStreamHost>>());
        await host.StartAsync(CancellationToken.None);

        try
        {
            // Delivered out of order on purpose: the later execution first, so the stream itself carries no ordering
            // the writer could lean on.
            stream.Arm(
                LateFill("7111", "late-exit-7111", "late-exit-fill-7111", OrderSide.Sell, 5_005m, At(2)),
                LateFill("7111", "late-entry-7111", "late-entry-fill-7111", OrderSide.Buy, 4_990m, At(1)));

            bool bothLanded = await WaitUntilAsync(async () =>
                await QueryDbAsync(database => database.Fills
                    .IgnoreQueryFilters()
                    .CountAsync(fill => fill.VenueFillKey == "late-exit-fill-7111"
                        || fill.VenueFillKey == "late-entry-fill-7111")) == 2);

            bothLanded.Should().BeTrue(
                "both late executions must be persisted through the real ingestion path before the replay, or the "
                + "case never reaches the re-pairing it exists to guard");

            stream.Arm(FlatAt("7111", At(3)));

            bool refused = await WaitUntilAsync(() => Task.FromResult(
                _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Any(measurement =>
                    (string?)measurement.Tags.GetValueOrDefault("outcome")
                        == ExecutionMetrics.JournalBoundaryMergeRefused)));

            refused.Should().BeTrue(
                "a window fill already journalled under a DIFFERENT pairing must refuse the flat and say so — "
                + "'boundary-merge-refused' is what makes an account stuck under-reporting visible to an alert "
                + "rather than only to a log");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        List<Trade> afterReplay = await TradesAsync(accountId);
        afterReplay.Should().ContainSingle(
            "the fail-closed direction is UNDER-reporting: one stale row is recoverable, a double-counted day is "
            + "headroom the operator never had");
        afterReplay[0].Id.Should().Be(survivingRowId, "the surviving row is the one already journalled, untouched");
        afterReplay[0].RealizedPnL.Should().Be(survivingPnL, "and its figure is not silently rewritten either");
    }

    // =================================================================================================================
    // 5. Same-instant opposite-side ambiguity (gh#799 bullet 7).
    // =================================================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessFlatAsync_ShouldWriteNoRow_WhenABuyAndASellShareTheExactExecutedAt(bool sellSortsFirst)
    {
        // A trip-1 close and a trip-2 open at ONE instant. Ordered by (ExecutedAt, Id) the FillId is the only thing
        // separating them, and it is an arbitrary venue handle: one order makes the window look like a closed trip
        // followed by a reopen, the other like a scale-in that never returned to flat -- opposite SIGNS for the same
        // executions. There is no correct answer to pick, so the only safe behaviour is to write nothing.
        //
        // The parameter is what makes this a guard rather than a coincidence: BOTH orders are exercised, so a
        // tie-break that silently resolved the ambiguity would pass one case and fail the other. Asserting a single
        // ordering would pass with and against the defect half the time.
        //
        // PROVE-RED: replace the refusal with a FillId tie-break and one of the two cases journals a row -- with a
        // sign that depends on nothing but which Guid the venue happened to mint first.
        int block = sellSortsFirst ? 12 : 13;
        string venueKey = sellSortsFirst ? "7112" : "7113";

        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, venueKey);

        Guid entry = FillId(block, 0x01);
        Guid tiedSell = FillId(block, sellSortsFirst ? (byte)0x10 : (byte)0x20);
        Guid tiedBuy = FillId(block, sellSortsFirst ? (byte)0x20 : (byte)0x10);
        Guid finalExit = FillId(block, 0x30);

        DateTimeOffset tie = At(1);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"entry-{venueKey}", [(entry, 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, $"tied-sell-{venueKey}", [(tiedSell, 5_010m, 1, tie)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"tied-buy-{venueKey}", [(tiedBuy, 5_010m, 1, tie)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, $"exit-{venueKey}", [(finalExit, 5_020m, 1, At(2))]);

        bool journalled = await JournalAsync(FlatAt(venueKey, At(2)));

        journalled.Should().BeFalse("an ambiguous window has no single right answer — refuse, never guess");
        (await TradesAsync(accountId)).Should().BeEmpty(
            "a row written here would carry a side, an entry and an exit chosen by nothing but Guid order, and its "
            + "P&L would enter the daily governor with a sign that could just as easily have been the other one "
            + "(R-4 / R-5)");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static DateTimeOffset At(int minutes) => _origin.AddMinutes(minutes);

    /// <summary>
    /// Central midnight for a calendar day, expressed in UTC — computed through <see cref="MarketClock"/> so the
    /// straddle case holds either side of a daylight-saving change, and normalised to offset 0 because Npgsql
    /// refuses a non-zero offset on a <c>timestamp with time zone</c> column.
    /// </summary>
    private static DateTimeOffset CentralMidnightUtc(int year, int month, int day)
    {
        DateTime midnight = new(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, MarketClock.CentralTime.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// A stable, ordered fill id. <paramref name="order"/> is the LAST byte — the last field .NET compares — so it
    /// alone drives the <c>(ExecutedAt, Id)</c> tie-break the ambiguity case parametrises. <paramref name="block"/>
    /// is a per-case id space: <c>Fill.Id</c> is a primary key and the container is shared by the whole class, so
    /// two cases seeding the same id would collide on insert.
    /// </summary>
    private static Guid FillId(int block, byte order) =>
        Guid.Parse($"{block:x8}-0000-0000-0000-0000000000{order:x2}");

    private static PositionEvent FlatAt(string venueAccountKey, DateTimeOffset at) =>
        new(VenueAccountId.Create(_projectx, venueAccountKey), at,
            VenueContractId.Create(_projectx, Contract), NetQuantity: 0, new Price(5_000m));

    private static FillEvent LateFill(
        string venueAccountKey,
        string venueOrderKey,
        string venueFillKey,
        OrderSide side,
        decimal price,
        DateTimeOffset at) =>
        new(VenueAccountId.Create(_projectx, venueAccountKey), at, venueOrderKey, venueFillKey, side,
            Quantity: 1, new Price(price), Fees: 0m, Voided: false);

    /// <summary>One composed leg, keyed the way the writer keys one — used where the INDEX rather than the writer is
    /// the subject, so the row goes straight at the context.</summary>
    private static Trade Leg(Guid owner, Guid accountId, Guid closingFillId, Guid openingFillId, decimal realized) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = accountId,
        Instrument = Contract,
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5_000m,
        ExitPrice = 4_980m,
        RealizedPnL = realized,
        Mode = TradingMode.Practice,
        ClosedAt = At(2),
        ClosingFillId = closingFillId,
        OpeningFillId = openingFillId,
    };

    /// <summary>The scale-in exited by ONE spanning fill: two legs, one shared closing fill, -100 and -50 at a point
    /// value of 5. The shape three cases share.</summary>
    private async Task SeedScaleInWithSpanningExitAsync(Guid owner, Guid accountId, string tag, int block)
    {
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"entry-a-{tag}", [(FillId(block, 0x01), 5_000m, 1, At(0))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, $"entry-b-{tag}", [(FillId(block, 0x02), 4_990m, 1, At(1))]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, $"exit-{tag}", [(FillId(block, 0x03), 4_980m, 2, At(2))]);
    }

    private async Task<bool> JournalAsync(PositionEvent flat)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<TradeJournalService>()
            .ProcessFlatAsync(flat, CancellationToken.None);
    }

    /// <summary>Scoped to ONE account: the fixture is a single container shared by the whole class, so an unscoped
    /// read makes each case see the others' rows and every count assertion becomes order-dependent.</summary>
    private async Task<List<Trade>> TradesAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Trades
            .IgnoreQueryFilters()
            .Where(trade => trade.AccountId == accountId)
            .OrderBy(trade => trade.ClosedAt)
            .ThenBy(trade => trade.RealizedPnL)
            .ToListAsync();
    }

    /// <summary>The daily governor's own read, through an OWNER-SCOPED context: <c>DailyRealizedReader</c> is
    /// R-20-filtered, so a context with no user reads nothing and the assertion would pass on an empty sum.</summary>
    private Task<decimal> TodayRealizedAsync(Guid owner, Guid accountId) => TodayRealizedAsync(owner, accountId, _readAt);

    private async Task<decimal> TodayRealizedAsync(Guid owner, Guid accountId, DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext database = new(options, new OwnerUser(owner));
        return await database.TodayRealizedPnLForAccountAsync(
            accountId, TradingMode.Practice, now, CancellationToken.None);
    }

    private async Task<ConsistencyWindow> ConsistencyWindowAsync(Guid owner, Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext database = new(options, new OwnerUser(owner));
        return await database.ConsistencyWindowForAccountAsync(
            accountId, TradingMode.Practice, CancellationToken.None);
    }

    /// <summary>A closed trade with NO natural key — the shape of a row journalled before the writer existed. It
    /// stays out of the filtered composite index, which is what the migration case leans on.</summary>
    private Task SeedHistoricalTradeAsync(Guid owner, Guid accountId, DateTimeOffset closedAt, decimal realized) =>
        ExecuteDbAsync(async database =>
        {
            database.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                ExitPrice = 5_040m,
                RealizedPnL = realized,
                Mode = TradingMode.Practice,
                ClosedAt = closedAt,
            });
            await database.SaveChangesAsync();
        });

    private async Task<Guid> SeedAccountAsync(Guid owner, string venueAccountKey)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async database =>
        {
            database.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
            database.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = OurCredentialKey,
            });
            database.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = venueAccountKey,
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            await database.SaveChangesAsync();
        });

        return accountId;
    }

    /// <summary>Seeds an executed order and its fills with EXPLICIT fill ids — the composition's tie-break reads
    /// them, so they cannot be left to chance. An empty <paramref name="fills"/> seeds a still-working order whose
    /// executions the venue has not reported yet.</summary>
    private async Task SeedOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        string venueOrderKey,
        (Guid Id, decimal Price, int Size, DateTimeOffset At)[] fills)
    {
        Guid orderId = Guid.NewGuid();

        await ExecuteDbAsync(async database =>
        {
            database.Orders.Add(new Order
            {
                Id = orderId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = side,
                Size = fills.Length == 0 ? 1 : fills.Sum(fill => fill.Size),
                Type = OrderType.Market,
                EntryPrice = fills.Length == 0 ? 5_000m : fills[0].Price,
                PointValue = ContractPointValue,
                TickSize = 0.25m,
                Status = fills.Length == 0 ? OrderStatus.Working : OrderStatus.Filled,
                Mode = TradingMode.Practice,
                VenueOrderKey = venueOrderKey,
                PlacedAt = fills.Length == 0 ? _origin : fills[0].At,
            });

            foreach ((Guid id, decimal price, int size, DateTimeOffset at) in fills)
            {
                database.Fills.Add(new Fill
                {
                    Id = id,
                    UserId = owner,
                    OrderId = orderId,
                    VenueFillKey = id.ToString("N"),
                    Price = price,
                    Size = size,
                    ExecutedAt = at,
                });
            }

            await database.SaveChangesAsync();
        });
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> stage)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await stage(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    /// <summary>Polls until <paramref name="condition"/> holds, returning <see langword="false"/> on timeout rather
    /// than throwing — the shape the other stream-driven suites use, so a genuine red reports as an assertion
    /// failure instead of an unhandled exception.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 200, int delayMs = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }
}
