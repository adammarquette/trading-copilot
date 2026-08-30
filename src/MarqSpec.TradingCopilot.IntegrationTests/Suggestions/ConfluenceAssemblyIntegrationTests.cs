using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.IntegrationTests.Api;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Suggestions;

/// <summary>
/// Independent QA integration coverage for the <b>confluence assembly</b> (gh#967 ⇒ gh#730, ADR-0026, R-4) against
/// <b>real Postgres</b>. The pure <c>ConfluenceAlignment</c> rule is unit-covered off the EF in-memory provider; this
/// suite proves the <b>scan → issuance</b> path that surrounds it — the DB CHECKs on the <c>CitedFactor</c> level arm
/// (<c>CK_SuggestionCitedFactors_KindColumns</c>), the venue-agnostic level read, and the fail-open posture — none of
/// which the in-memory tier can witness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authored independently, from the issue spec</b> (gh#967's "What only real Postgres proves"), not from the
/// gh#730 implementation (QA contract §Role). Per the adversarial-double rule, every case <b>feeds</b> raw inputs —
/// seeded <c>IndicatorValueRecord</c> rows and <c>PriceLevel</c> zones — and never hands the system a
/// production-computed <c>ConfluenceFactor</c> / <c>CitedFactor</c> set.
/// </para>
/// <para>
/// <b>Driven through the shipped composition.</b> The scan runs through the real
/// <c>TriggerEvaluationService.ScanAsync(now, …)</c> via the shared <see cref="AgentReviewFixture"/> plumbing (gh#429),
/// reusing <see cref="AgentReviewTestPostgresFactory"/> AS-IS: it configures an LLM (so the real reviewer is under
/// test) and leaves <c>ConfluenceOptions</c> at its shipped default (ladder <c>[5, 15, 60, 240, 1440]</c>,
/// <c>KTicks=8</c>, <c>FAtr=0.5</c> ⇒ a tick-only band of <c>8 × 0.25 = 2.00</c> for ES with no ATR seeded) — no
/// suite-specific host is needed. The only doubled seam is the outbound <see cref="AdversarialLlmProvider"/>; the
/// reviewer, <c>StoredIndicatorSource</c>, <c>StoredPriceLevelSource</c>, <c>ConfluenceAlignment</c>,
/// <c>CitedFactorSet.DerivePrimary</c> and the whole scan are production code. Trigger / indicator-value / price-level
/// seeding is this suite's own (not <see cref="AgentReviewFixture"/>'s fixed ES/rsi/14/5m helpers), so each case can
/// vary the fired resolution, the corroborating timeframes and the level geometry independently.
/// </para>
/// <para>
/// <b>Prove-the-red, and where it was witnessed</b> (recorded here because the exercises are temporary local edits,
/// never committed): (1) multi-factor composition — forcing <c>AssembleSupportingAsync</c> to <c>return []</c>
/// unconditionally collapsed the 3-factor case to 1. (2) primary-stays-fired — feeding the <b>full</b> cited-factor
/// list (indicators + levels) into <c>CitedFactorSet.DerivePrimary</c> instead of the indicator-only list let the
/// smaller-timeframe LEVEL steal <c>IsPrimary</c>. (3) retired-level exclusion — dropping <c>&amp;&amp; level.Active</c>
/// from <c>StoredPriceLevelSource</c>'s venue-agnostic query cited the retired zone and broke N=1. (4) fail-open —
/// with a thrown fault on a spec miss AND the surrounding try/catch in <c>AssembleSupportingAsync</c> removed, the
/// whole owner pass aborted: the co-located mechanical trigger's arm never reached <c>Fired</c> either, which is
/// exactly what the try/catch (the real fail-open mechanism) prevents. (5) R-4 "no FK" — manually adding a scratch
/// <c>FOREIGN KEY ("LevelId") REFERENCES "PriceLevels"("Id")</c> to the running test container made the DELETE step
/// throw a Postgres FK-violation; dropping the scratch constraint restored green — the merge/flip/retire steps
/// guard a fact true <b>by construction</b> (nothing reads <c>CitedFactor</c>'s level columns from anywhere but the
/// row itself), so their red is this schema fact, not a runtime branch. (6a) venue-agnostic — reinstating a
/// <c>&amp;&amp; level.Venue == "projectx"</c> filter on the 2-arg <c>GetActiveLevelsAsync</c> dropped the cited set
/// from 2 venues to 1. (6b) R-20 — skipping <c>CitedFactor</c> in <c>TenantDbContext</c>'s <c>IUserOwned</c> filter
/// loop let a stranger-owner-scoped context read the firing owner's factor rows. Every edit was reverted with
/// <c>git checkout</c> (the test committed first) before the suite shipped.
/// </para>
/// <para>
/// <b>Tier:</b> pre-merge, container-backed Postgres (real migrations, real check constraints). Venue-independent —
/// confluence touches no venue; <see cref="AgentReviewTestPostgresFactory"/>'s adversarial venue stub is never called
/// on any path this suite exercises.
/// </para>
/// </remarks>
public class ConfluenceAssemblyIntegrationTests : IClassFixture<AgentReviewTestPostgresFactory>
{
    private const string Symbol = "ES"; // preconfigured in InstrumentSpecOptions: tick size 0.25 -> band 8*0.25=2.00
    private const string UnconfiguredSymbol = "ZZQATEST"; // deliberately absent from InstrumentSpecOptions
    private const string Indicator = "rsi";
    private const int Period = 14;
    private const decimal Threshold = 70m;
    private const int TriggerSize = 2;
    private const string PrimaryVenue = "projectx";

    private static readonly DateTimeOffset _now = AgentReviewFixture.Now;

    private readonly AgentReviewTestPostgresFactory _factory;
    private readonly AgentReviewFixture _fixture;

    public ConfluenceAssemblyIntegrationTests(AgentReviewTestPostgresFactory factory)
    {
        _factory = factory;
        _fixture = new AgentReviewFixture(factory.Services, factory.CreateClient);
    }

    // =============================================================================================================
    // 1. A real multi-factor suggestion composes (gh#967 bullet 1).
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldComposeAMultiFactorSuggestion_FromACorroboratingIndicatorAndAnInBandLevel()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        // Primary fires at 5m; the SAME signal at 15m (a HIGHER rung) also satisfies the threshold and corroborates;
        // a level at 60m sits inside the tick-only band (8 * 0.25 = 2.00) around the model's 5,000 entry.
        await SeedTriggerAsync(userId, accountId, Symbol, resolutionMinutes: 5);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 5, value: 75m);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 15, value: 80m);
        Guid levelId = await SeedPriceLevelAsync(
            Symbol, PrimaryVenue, timeframeMinutes: 60, top: 5_001m, bottom: 4_999m, significance: 12.5m);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(1, "the arming edge fired");
        Suggestion suggestion = await SingleSuggestionWithFactorsAsync();
        suggestion.CitedFactors.Should().HaveCount(
            3, "one primary indicator, one corroborating indicator and one in-band level");

        CitedFactor primary = suggestion.CitedFactors.Should().ContainSingle(f => f.IsPrimary).Which;
        primary.Kind.Should().Be(CitedFactorKind.Indicator, "a level never fires, so it can never be the primary");
        primary.TimeframeMinutes.Should().Be(5, "the primary stays the FIRED timeframe");
        primary.Indicator.Should().Be(Indicator);
        primary.Period.Should().Be(Period);

        CitedFactor supportingIndicator = suggestion.CitedFactors.Should()
            .ContainSingle(f => f.Kind == CitedFactorKind.Indicator && !f.IsPrimary).Which;
        supportingIndicator.TimeframeMinutes.Should().Be(15);
        supportingIndicator.Indicator.Should().Be(Indicator);
        supportingIndicator.Period.Should().Be(Period);

        // The level factor round-tripped through the REAL CK_SuggestionCitedFactors_KindColumns pairing check: a
        // mapping that paired the wrong columns to Kind=Level (e.g. left LevelSignificance null, or filled Indicator)
        // would 500 the SaveChanges above on real Postgres and this suite would never reach this assertion at all —
        // the InMemory tier cannot fail this way, which is exactly the gap gh#967 calls out.
        CitedFactor level = suggestion.CitedFactors.Should().ContainSingle(f => f.Kind == CitedFactorKind.Level).Which;
        level.IsPrimary.Should().BeFalse("a level never fires, so it is never the primary (ADR-0026)");
        level.TimeframeMinutes.Should().Be(60);
        level.LevelId.Should().Be(levelId);
        level.LevelVenue.Should().Be(PrimaryVenue);
        level.LevelKind.Should().Be((int)PriceLevelKind.Support);
        level.LevelTop.Should().Be(5_001m);
        level.LevelBottom.Should().Be(4_999m);
        level.LevelSignificance.Should().Be(12.5m);
        level.Indicator.Should().BeNull("the level arm and the indicator arm are mutually exclusive");
        level.Period.Should().BeNull();
    }

    // =============================================================================================================
    // 2. The primary stays the fired signal, even with a smaller-timeframe supporting level (gh#967 bullet 2).
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldKeepTheFiredIndicatorPrimary_WhenACorroboratingLevelSitsOnASmallerTimeframe()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        // Fires at 15m (a HIGHER rung of the ladder than the level below) with NO other indicator readings seeded,
        // so the only supporting factor is the level -- isolating "does a level ever outrank the fired signal" from
        // indicator corroboration, which case 1 already covers. The level sits at 5m: SMALLER than the fired 15m.
        await SeedTriggerAsync(userId, accountId, Symbol, resolutionMinutes: 15);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 15, value: 75m);
        await SeedPriceLevelAsync(Symbol, PrimaryVenue, timeframeMinutes: 5, top: 5_000.5m, bottom: 4_999.5m);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        await _fixture.ScanAsync();

        Suggestion suggestion = await SingleSuggestionWithFactorsAsync();
        suggestion.CitedFactors.Should().HaveCount(2, "one primary indicator plus one in-band level");

        CitedFactor primary = suggestion.CitedFactors.Should()
            .ContainSingle(f => f.Kind == CitedFactorKind.Indicator).Which;
        primary.IsPrimary.Should().BeTrue("the fired signal is the only INDICATOR-arm candidate, so it stays primary");
        primary.TimeframeMinutes.Should().Be(15, "the fired timeframe, unchanged by the smaller level below it");

        CitedFactor level = suggestion.CitedFactors.Should().ContainSingle(f => f.Kind == CitedFactorKind.Level).Which;
        level.TimeframeMinutes.Should().Be(5, "numerically SMALLER than the fired 15m primary");
        level.IsPrimary.Should().BeFalse(
            "levels are excluded from the min-timeframe primary derivation entirely -- a smaller timeframe never lets a level steal the headline");
    }

    // =============================================================================================================
    // 3. N=1 degenerate: nothing corroborates and no level is IN BAND (gh#967 bullet 3).
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldStageTheDegenerateN1Suggestion_WhenNothingCorroboratesAndTheOnlyNearbyLevelIsRetired()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        await SeedTriggerAsync(userId, accountId, Symbol, resolutionMinutes: 5);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 5, value: 75m);

        // A level that WOULD be in-band of the 5,000 entry, but retired (Active=false): only real Postgres proves
        // the venue-agnostic read's "retired levels are excluded" clause end-to-end, since a wrongly-included row
        // here is exactly what would turn this "no in-band level" case into a false 2-factor suggestion.
        await SeedPriceLevelAsync(Symbol, PrimaryVenue, timeframeMinutes: 60, top: 5_001m, bottom: 4_999m, active: false);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(1);
        Suggestion suggestion = await SingleSuggestionWithFactorsAsync();

        // Byte-for-byte today's single primary Indicator factor (gh#592's pre-confluence shape): exactly one row,
        // the fired read, nothing from the level arm at all.
        CitedFactor only = suggestion.CitedFactors.Should().ContainSingle().Which;
        only.Kind.Should().Be(CitedFactorKind.Indicator);
        only.IsPrimary.Should().BeTrue();
        only.TimeframeMinutes.Should().Be(5);
        only.Indicator.Should().Be(Indicator);
        only.Period.Should().Be(Period);
        only.LevelId.Should().BeNull();
        only.LevelVenue.Should().BeNull();
        only.LevelKind.Should().BeNull();
        only.LevelTop.Should().BeNull();
        only.LevelBottom.Should().BeNull();
        only.LevelSignificance.Should().BeNull();
    }

    // =============================================================================================================
    // 4. Fail-open: an unresolvable instrument spec degrades to N=1 without aborting the pass (gh#967 bullet 4).
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldDegradeToN1AndKeepRunning_WhenTheFiredInstrumentsSpecIsUnresolvable()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        // A co-located MECHANICAL trigger for the SAME owner, on a normal symbol -- proves the scan pass is not
        // aborted and the mechanical route still runs alongside the fault below (both share one per-owner
        // SaveChanges; an uncaught fault on EITHER trigger would discard the whole pass, including this one's
        // journal and arm advance).
        await SeedTriggerAsync(userId, null, Symbol, resolutionMinutes: 5, route: TriggerRoute.Mechanical);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 5, value: 75m);

        // The agent-review fire, on a symbol that is NOT in InstrumentSpecOptions: TryResolve misses, so the tick
        // size/ATR/level reads are all skipped (the fail-closed "cannot measure -> do not fabricate" posture) while
        // the fire itself must still issue -- the "unresolvable spec" alternative gh#967 names for fail-open.
        await SeedTriggerAsync(userId, accountId, UnconfiguredSymbol, resolutionMinutes: 5);
        await SeedIndicatorAsync(UnconfiguredSymbol, resolutionMinutes: 5, value: 75m);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(2, "both armed edges fired this pass");
        (await ArmStateForSymbolAsync(userId, Symbol)).Should().Be(
            TriggerArmState.Fired, "the co-located MECHANICAL trigger's journal + arm advance must survive the other trigger's spec miss");
        (await ArmStateForSymbolAsync(userId, UnconfiguredSymbol)).Should().Be(
            TriggerArmState.Fired, "the agent-review trigger itself still fired and advanced, despite the spec miss");
        (await TriggerFiringCountAsync(userId)).Should().Be(2, "a fire is a fire regardless of the confluence outcome");

        Suggestion suggestion = await SingleSuggestionWithFactorsAsync();
        suggestion.Instrument.Should().Be(UnconfiguredSymbol);
        suggestion.CitedFactors.Should().ContainSingle(
            "an unresolvable spec degrades confluence to the N=1 single primary factor -- it must never abort the fire");
    }

    // =============================================================================================================
    // 5. R-4 immutability, end-to-end: merge / flip / retire / delete the SOURCE PriceLevel (gh#967 bullet 5).
    // =============================================================================================================

    [Fact]
    public async Task Suggestion_ShouldKeepItsCitedLevelSnapshotFrozen_AcrossMergeFlipRetireAndDeleteOfTheSourceLevel()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        await SeedTriggerAsync(userId, accountId, Symbol, resolutionMinutes: 5);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 5, value: 75m);
        Guid levelId = await SeedPriceLevelAsync(
            Symbol, PrimaryVenue, timeframeMinutes: 60, top: 5_001m, bottom: 4_999m, significance: 12.5m);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);
        await _fixture.ScanAsync();

        Suggestion issued = await SingleSuggestionWithFactorsAsync();
        Guid suggestionId = issued.Id;
        CitedFactor asIssued = issued.CitedFactors.Should().ContainSingle(f => f.Kind == CitedFactorKind.Level).Which;

        // The frozen snapshot as issued -- what every mutation below must leave untouched (a soft LevelId reference
        // copied at issuance, R-4; no FK to PriceLevels).
        decimal originalTop = asIssued.LevelTop!.Value;
        decimal originalBottom = asIssued.LevelBottom!.Value;
        int originalKind = asIssued.LevelKind!.Value;
        decimal originalSignificance = asIssued.LevelSignificance!.Value;
        string originalVenue = asIssued.LevelVenue!;

        async Task AssertSnapshotUnchangedAsync(string because)
        {
            CitedFactor current = await CitedLevelFactorAsync(suggestionId);
            current.LevelTop.Should().Be(originalTop, because);
            current.LevelBottom.Should().Be(originalBottom, because);
            current.LevelKind.Should().Be(originalKind, because);
            current.LevelSignificance.Should().Be(originalSignificance, because);
            current.LevelVenue.Should().Be(originalVenue, because);
        }

        // MERGE: an aligned pivot widens the zone and bumps TouchCount/Significance.
        await MutatePriceLevelAsync(levelId, level =>
        {
            level.Top = 5_010m;
            level.Bottom = 4_990m;
            level.TouchCount = 3;
            level.Significance = 20m;
            level.UpdatedAt = _now;
        });
        await AssertSnapshotUnchangedAsync("a merge of the source level must not move the frozen snapshot");

        // FLIP: the detector reverses the zone's side (a broken support becoming resistance).
        await MutatePriceLevelAsync(levelId, level => level.Kind = PriceLevelKind.Resistance);
        await AssertSnapshotUnchangedAsync("flipping the source level's side must not move the frozen snapshot");

        // RETIRE: evicted from the active read.
        await MutatePriceLevelAsync(levelId, level => level.Active = false);
        await AssertSnapshotUnchangedAsync("retiring the source level must not move the frozen snapshot");

        // DELETE: the source row is gone outright. LevelId is a SOFT reference (no FK to PriceLevels) -- this must
        // neither cascade the cited factor away nor block on a constraint.
        Func<Task> delete = () => _factory.WithDatabaseAsync(async database =>
        {
            await database.PriceLevels.Where(level => level.Id == levelId).ExecuteDeleteAsync();
            return 0;
        });
        await delete.Should().NotThrowAsync(
            "CitedFactor.LevelId carries no FK to PriceLevels (R-4) -- the source may vanish outright");

        Suggestion stillReads = await SingleSuggestionWithFactorsAsync();
        stillReads.Id.Should().Be(suggestionId, "the suggestion still reads after its cited level's source row is gone");
        await AssertSnapshotUnchangedAsync("deleting the source level outright must not move the frozen snapshot either");
    }

    // =============================================================================================================
    // 6. Venue-agnostic level read, and the suggestion it feeds stays owner-scoped (gh#967 bullet 6, R-20).
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldCiteLevelsAcrossEveryVenue_WhileTheSuggestionAndItsFactorsStayOwnerScoped()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        await SeedTriggerAsync(userId, accountId, Symbol, resolutionMinutes: 5);
        await SeedIndicatorAsync(Symbol, resolutionMinutes: 5, value: 75m);

        // Two levels, same instrument + timeframe, found on DIFFERENT venues. A trigger stores no venue at all, so
        // the venue-agnostic overload must return BOTH -- mirroring StoredIndicatorSource's venue-neutral read.
        await SeedPriceLevelAsync(Symbol, "projectx", timeframeMinutes: 60, top: 5_001m, bottom: 4_999m);
        await SeedPriceLevelAsync(Symbol, "tradovate", timeframeMinutes: 60, top: 5_001.5m, bottom: 4_998.5m);

        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);
        await _fixture.ScanAsync();

        Suggestion issued = await SingleSuggestionWithFactorsAsync();
        List<CitedFactor> levelFactors = [.. issued.CitedFactors.Where(f => f.Kind == CitedFactorKind.Level)];
        levelFactors.Should().HaveCount(2, "the venue-agnostic read returns the level from EVERY venue, not just one");
        levelFactors.Select(f => f.LevelVenue).Should().BeEquivalentTo(["projectx", "tradovate"]);
        levelFactors.Should().OnlyContain(f => !f.IsPrimary);

        // R-20: the suggestion (and its factor set) this shared, venue-spanning market data fed stays visible ONLY
        // to the firing owner -- the default-deny filter, not the shared-data exemption that let both venues' levels
        // in above. Built exactly as production scopes a per-owner context (TriggerEvaluationService.ProcessOwnerAsync).
        // DbContextOptions<T> is registered SCOPED (not singleton), so it must be resolved from a scope -- the
        // options object itself is plain configuration, so it stays usable after the resolving scope is disposed.
        using IServiceScope resolvingScope = _factory.Services.CreateScope();
        DbContextOptions<TradingCopilotDbContext> options =
            resolvingScope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();

        await using (TradingCopilotDbContext strangerScope = new(options, new OwnerUser(Guid.NewGuid())))
        {
            (await strangerScope.Suggestions.CountAsync(s => s.Id == issued.Id)).Should().Be(
                0, "R-20 default-deny: a suggestion fed by shared, venue-agnostic level data must still be invisible to another owner");
            (await strangerScope.CitedFactors.CountAsync(f => f.SuggestionId == issued.Id)).Should().Be(
                0, "CitedFactor is IUserOwned too -- another owner's scoped context must see none of its rows either");
        }

        await using (TradingCopilotDbContext ownerScope = new(options, new OwnerUser(userId)))
        {
            (await ownerScope.Suggestions.CountAsync(s => s.Id == issued.Id)).Should().Be(
                1, "the firing owner's own scoped context reads the suggestion normally");
            (await ownerScope.CitedFactors.CountAsync(f => f.SuggestionId == issued.Id)).Should().Be(3);
        }
    }

    // =============================================================================================================
    // Helpers -- this suite's OWN seeding (not AgentReviewFixture's fixed ES/rsi/14/5m helpers), so each case can
    // vary the fired resolution, instrument and level geometry independently. Feeds raw inputs only, per the QA
    // adversarial-double rule: no helper here ever hands the system a pre-computed ConfluenceFactor/CitedFactor.
    // =============================================================================================================

    private async Task ResetAsync()
    {
        _factory.Llm.Reset();
        _factory.Pushover.Reset();
        await _factory.ClearOutboxAsync();
        await _fixture.ClearAsync();
        await _factory.WithDatabaseAsync(async database =>
        {
            // AgentReviewFixture.ClearAsync() does not know about PriceLevels -- this suite's own addition.
            await database.PriceLevels.ExecuteDeleteAsync();
            return 0;
        });
    }

    private Task SeedTriggerAsync(
        Guid userId, Guid? accountId, string symbol, int resolutionMinutes,
        TriggerRoute route = TriggerRoute.AgentReview) =>
        _factory.WithDatabaseAsync(async database =>
        {
            database.Triggers.Add(new TriggerRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Symbol = symbol,
                Indicator = Indicator,
                Period = Period,
                ResolutionMinutes = resolutionMinutes,
                ConditionKind = TriggerConditionKind.IndicatorThreshold,
                Comparison = IndicatorComparison.Above,
                Threshold = Threshold,
                Route = route,
                Severity = NotificationSeverity.Notify,
                Enabled = true,
                Confirmation = TriggerConfirmation.Confirmed,
                ArmState = TriggerArmState.Armed,
                ArmCycle = 0,
                AccountId = route == TriggerRoute.AgentReview ? accountId : null,
                Size = route == TriggerRoute.AgentReview ? TriggerSize : null,
                CreatedAt = _now.AddDays(-1),
            });
            await database.SaveChangesAsync();
            return 0;
        });

    private Task SeedIndicatorAsync(string symbol, int resolutionMinutes, decimal value) =>
        _factory.WithDatabaseAsync(async database =>
        {
            database.IndicatorValues.Add(new IndicatorValueRecord
            {
                Venue = PrimaryVenue,
                Instrument = symbol,
                ResolutionMinutes = resolutionMinutes,
                Indicator = Indicator,
                Period = Period,
                BucketStart = _now.AddMinutes(-resolutionMinutes),
                Value = value,
                RecordedAt = _now.AddMinutes(-resolutionMinutes),
            });
            await database.SaveChangesAsync();
            return 0;
        });

    private Task<Guid> SeedPriceLevelAsync(
        string symbol,
        string venue,
        int timeframeMinutes,
        decimal top,
        decimal bottom,
        PriceLevelKind kind = PriceLevelKind.Support,
        decimal significance = 10m,
        bool active = true) =>
        _factory.WithDatabaseAsync(async database =>
        {
            Guid id = Guid.NewGuid();
            database.PriceLevels.Add(new PriceLevel
            {
                Id = id,
                Venue = venue,
                Instrument = symbol,
                TimeframeMinutes = timeframeMinutes,
                Top = top,
                Bottom = bottom,
                Kind = kind,
                Significance = significance,
                FormedAtBucket = _now.AddHours(-2),
                TouchCount = 2,
                Active = active,
                UpdatedAt = _now.AddHours(-1),
            });
            await database.SaveChangesAsync();
            return id;
        });

    private Task MutatePriceLevelAsync(Guid levelId, Action<PriceLevel> mutate) =>
        _factory.WithDatabaseAsync(async database =>
        {
            PriceLevel level = await database.PriceLevels.SingleAsync(l => l.Id == levelId);
            mutate(level);
            await database.SaveChangesAsync();
            return 0;
        });

    private Task<Suggestion> SingleSuggestionWithFactorsAsync() =>
        _factory.WithDatabaseAsync(database => database.Suggestions
            .IgnoreQueryFilters()
            .Include(s => s.CitedFactors)
            .SingleAsync());

    private Task<CitedFactor> CitedLevelFactorAsync(Guid suggestionId) =>
        _factory.WithDatabaseAsync(database => database.CitedFactors
            .IgnoreQueryFilters()
            .SingleAsync(f => f.SuggestionId == suggestionId && f.Kind == CitedFactorKind.Level));

    private Task<TriggerArmState> ArmStateForSymbolAsync(Guid userId, string symbol) =>
        _factory.WithDatabaseAsync(database => database.Triggers
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.Symbol == symbol)
            .Select(t => t.ArmState)
            .SingleAsync());

    private Task<int> TriggerFiringCountAsync(Guid userId) =>
        _factory.WithDatabaseAsync(database => database.TriggerFirings
            .IgnoreQueryFilters()
            .CountAsync(firing => firing.UserId == userId));
}
