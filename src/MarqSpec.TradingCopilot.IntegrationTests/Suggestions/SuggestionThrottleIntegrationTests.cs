using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.Api;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Suggestions;

/// <summary>
/// Pre-merge integration coverage for the <b>R-4 suggestion-throttle wiring</b> (gh#745 ⇒ gh#551 / gh#588 / gh#587,
/// R-4 / R-5, ADR-0008) against <b>real Postgres</b>. The wiring shipped in #744 with full <b>unit</b> coverage on the
/// EF <b>in-memory</b> provider; two classes of behaviour are invisible to that provider and are what this suite
/// exists for — authored independently of the implementation, from the spec (gh#745, gh#551, R-4/R-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The real-Postgres blind spots</b> the in-memory tier cannot witness: (1) the realized-P&amp;L
/// <b>Central-trading-day boundary</b> — <c>DailyRealizedReader</c> does a coarse SQL two-day pre-filter and then an
/// <b>in-memory</b> <c>MarketClock</c> date match (the timezone conversion has no EF translation), so only real PG
/// exercises the pre-filter + round-trip; and (2) the throttle's <b>issuance-count window</b> keyed on
/// <c>Suggestions.CreatedAt &gt;= CentralDayStartUtc(now)</c> under the R-20 owner filter, and the within-pass
/// change-tracker tally that keeps several same-account fires in one <c>SaveChanges</c> from each slipping under the
/// cap (the gh#455 shared-transaction pattern).
/// </para>
/// <para>
/// <b>Driven through the shipped composition.</b> The scan runs through the real
/// <c>TriggerEvaluationService.ScanAsync(now, …)</c> at a fixed instant (the service never reads a clock), reusing the
/// agent-review route's <see cref="AgentReviewFixture"/> plumbing (gh#429) — the throttle's only live consumer — so
/// this suite differs from the AI-spend one only in what it seeds (realized P&amp;L + a declared governor) and in the
/// throttle being opted in (<see cref="SuggestionThrottleTestPostgresFactory"/>). The single doubled seam is the
/// outbound <see cref="TestHost.AdversarialLlmProvider"/>; the reviewer, the <c>SuggestionThrottle</c> policy, the
/// realized-P&amp;L reader, the scan and the whole notification chain are production code.
/// </para>
/// <para>
/// <b>Prove-the-red, and where it was witnessed.</b> Each case is constructed so that breaking the guard it covers
/// flips the assertion — every prove-red is stated in-line as "were X wrong, this would read Y". The wiring is on
/// <c>develop</c>, so this suite is a set of <b>regression guards</b> that are green on this base and go red if the
/// boundary, the owner scope, the within-pass tally, the suppress-before-spend ordering or the host-boot validation
/// regress. The authoring sandbox has <b>no Docker daemon</b>, so the container tier could not be run locally (the
/// same constraint <see cref="RealizedReaderModeFilterIntegrationTests"/> records); the <c>integration tests
/// (pre-merge)</c> job on this PR is the observation of record. <b>No production code is touched by this PR</b> (QA
/// contract, guard discipline rule 3).
/// </para>
/// <para>
/// <b>Safety invariant kept front-of-mind:</b> the throttle may only ever <b>reduce or suppress</b> issuance, never
/// increase it, and a model-authored confidence can never lift the headroom-derived cap — the deterministic arm is
/// binding. Every case below either holds issuance flat/down or suppresses; none can make the throttle issue
/// <i>more</i>.
/// </para>
/// <para>
/// <b>Tier:</b> pre-merge, container-backed Postgres (real migrations, real check constraints and the R-14 mode
/// filter #755 landed). Venue-independent — the throttle touches no venue.
/// </para>
/// </remarks>
public class SuggestionThrottleIntegrationTests : IClassFixture<SuggestionThrottleTestPostgresFactory>
{
    private readonly SuggestionThrottleTestPostgresFactory _factory;
    private readonly AgentReviewFixture _fixture;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SuggestionThrottleIntegrationTests(SuggestionThrottleTestPostgresFactory factory)
    {
        _factory = factory;
        _fixture = new AgentReviewFixture(factory.Services, factory.CreateClient);
    }

    // The scan evaluates as of AgentReviewFixture.Now = 2026-07-29 15:00:00Z. Late July is CDT (UTC-5), so that is
    // 10:00 CT on 2026-07-29, and the Central trading day for the pass opens at 2026-07-29 00:00 CDT = 05:00:00Z. The
    // instants below straddle that boundary; keeping them explicit (rather than recomputing production's own
    // CentralDayStartUtc) is deliberate — a test that reimplemented the conversion could drift with the code it guards.

    /// <summary>6:00pm CT on 2026-07-28 (23:00Z) — LAST evening's session, the prior Central day. Excluded from today.</summary>
    private static readonly DateTimeOffset _lastEveningUtc = new(2026, 7, 28, 23, 0, 0, TimeSpan.Zero);

    /// <summary>00:01 CT on 2026-07-29 (05:01Z) — one minute INTO today's Central day. Included.</summary>
    private static readonly DateTimeOffset _justAfterCentralMidnightUtc = new(2026, 7, 29, 5, 1, 0, TimeSpan.Zero);

    /// <summary>07:00 CT on 2026-07-29 (12:00Z) — squarely inside today's Central day, before the 10:00 CT scan.</summary>
    private static readonly DateTimeOffset _todayMidMorningUtc = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 11:00pm CT on 2026-07-28 (04:00Z on 07-29) — AFTER UTC midnight but BEFORE the 05:00Z Central open, so the
    /// PRIOR Central trading day. This is the ONE band that discriminates a Central-day reading from a plain UTC-date
    /// one: a UTC-date reader buckets 04:00Z under 07-29 (today); the Central reader attributes it to yesterday.
    /// </summary>
    private static readonly DateTimeOffset _beforeCentralOpenUtc = new(2026, 7, 29, 4, 0, 0, TimeSpan.Zero);

    /// <summary>00:30 CT on 2026-07-29 (05:30Z) — just past the Central open; today's day.</summary>
    private static readonly DateTimeOffset _suggestionTodayUtc = new(2026, 7, 29, 5, 30, 0, TimeSpan.Zero);

    // =============================================================================================================
    // 1. The realized-P&L Central-trading-day boundary (gh#745 blind spot 1) — the reader's coarse SQL pre-filter +
    //    in-memory MarketClock date match, which the in-memory provider cannot witness.
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldProposeNormally_WhenAnEveningLossBelongsToThePriorCentralDay()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);

        // A full-governor loss taken at 6pm CT last night. The venue's session already rolled to a new day at ~5pm CT,
        // but the reader attributes by CENTRAL CALENDAR day, so this belongs to 2026-07-28 and must not deplete today's
        // headroom (the gh#11 reader-vs-venue divergence #745 asks be made explicit). It sits inside the reader's coarse
        // two-day SQL floor, so real PG PULLS it and the in-memory Central-date match is what EXCLUDES it.
        await SeedClosedTradeAsync(userId, accountId, mode, _lastEveningUtc, realized: -1_000m);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(1, "the arming edge fired");
        // This instant is yesterday under BOTH a Central-day and a plain UTC-date reading (UTC date 07-28 ≠ 07-29), so
        // it does NOT discriminate the two — the post-UTC-midnight case below does. What it uniquely guards is that the
        // in-memory Central-date MATCH runs at all: drop it and the coarse two-day SQL floor alone would PULL this loss
        // into today, read headroom 0, and SUPPRESS. A proposal is staged because the match kept yesterday in yesterday.
        (await _fixture.SuggestionCountAsync()).Should().Be(
            1, "an evening loss from the prior Central day leaves today's headroom full — the engine proposes normally");
        _factory.Llm.CallCount.Should().Be(1, "Full headroom wakes the reviewer");
        (await PausedAdvisoryCountAsync()).Should().Be(0, "nothing is suppressed, so the operator is never told suggestions paused");
    }

    [Fact]
    public async Task Scan_ShouldProposeNormally_WhenALossFallsInThePostUtcMidnightPreCentralOpenBand()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);

        // THE boundary discriminator (per the #812 review): 04:00Z on 2026-07-29 is after UTC midnight but before the
        // 05:00Z Central open — 23:00 CT on 2026-07-28, the PRIOR Central trading day. A UTC-DATE reader buckets it
        // under 07-29 (today) and would count this full-governor loss, driving headroom to 0 and SUPPRESSING the fire;
        // the Central reader attributes it to yesterday and leaves today's headroom full. So a proposal staged here is
        // the boundary prove-red the evening case above cannot give — that instant is yesterday under both readings,
        // whereas this one parts them. (It also still guards the in-memory match: dropping it lets the two-day floor
        // pull this loss into today and suppress.)
        await SeedClosedTradeAsync(userId, accountId, mode, _beforeCentralOpenUtc, realized: -1_000m);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(1, "the arming edge fired");
        (await _fixture.SuggestionCountAsync()).Should().Be(
            1, "a loss in the post-UTC-midnight, pre-Central-open band belongs to yesterday's Central day — today's headroom stays full");
        _factory.Llm.CallCount.Should().Be(1, "Full headroom wakes the reviewer");
        (await PausedAdvisoryCountAsync()).Should().Be(
            0, "the Central reader keeps yesterday's loss in yesterday; a UTC-date reader would suppress here — that is the boundary");
    }

    [Fact]
    public async Task Scan_ShouldSuppressBeforeAnySpend_WhenTodaysLossReachesTheGovernor()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);

        // A loss closed one minute after the Central open belongs to today, and it exactly reaches the 1,000 governor
        // (headroom fraction 0 → Suppressed, GovernorReached). This instant is today under BOTH readings — a UTC day
        // for 07-29 opens at 00:00Z (19:00 CT the prior day), EARLIER than the 05:00Z Central open, so it is NOT a
        // UTC-vs-Central discriminator (the 04:00Z case above is that). It still guards inclusion just inside the
        // Central open and that a timestamptz round-trip does not shift the instant back across it; its primary job
        // here is the suppress-before-spend path below.
        await SeedClosedTradeAsync(userId, accountId, mode, _justAfterCentralMidnightUtc, realized: -1_000m);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        // SUPPRESS-BEFORE-SPEND (gh#745 area 3), end to end: no LLM call, no AIUsage row, yet the fire is journaled,
        // the arm advances to Fired, and exactly ONE "Suggestions paused" advisory is made durable.
        fires.Should().Be(1, "a suppressed setup still fired — a fire is journaled whatever the outcome");
        _factory.Llm.CallCount.Should().Be(0, "the governor is reached, so no model call is made — capping WHETHER a call happens is the point");
        (await AiUsageCountAsync()).Should().Be(0, "no call means no cost, so no AIUsage row is written");
        (await _fixture.SuggestionCountAsync()).Should().Be(0, "a suppressed setup stages no proposal");
        (await _fixture.ArmStateAsync(userId)).Should().Be(
            TriggerArmState.Fired, "the arm still advances — the setup is reviewed-once, never retried into a suppression storm");
        (await PausedAdvisoryCountAsync()).Should().Be(
            1, "silence is never unexplained — the operator gets exactly one advisory that suggestions paused, per arming edge");
        (await OutboxBodiesAsync()).Should().ContainMatch(
            "*governor*", "the advisory quotes the throttle's own reason — the daily-drawdown governor is reached");
    }

    [Fact]
    public async Task Scan_ShouldSuppressBeforeAnySpend_WhenTheDailyTargetIsHitWithStandDown()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        // Stand-down ON with a 500 target, and a governor with ample room — so the suppression is the R-5 daily-target
        // stand-down, NOT the governor. A green day past target with stand-down OFF would stay Full; the flag is the
        // only difference, so this pins that the stand-down branch reads the flag rather than the profit alone.
        await DeclareRiskProfileAsync(accountId, governor: 5_000m, dailyProfitTarget: 500m, standDown: true);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);

        // A realized PROFIT today that reaches the target. Headroom is full (a green day), so only the stand-down can
        // suppress here — were the flag ignored, this would propose.
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: 500m);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(1, "the edge fired");
        _factory.Llm.CallCount.Should().Be(0, "standing down at the daily target makes no model call");
        (await AiUsageCountAsync()).Should().Be(0, "no call, no cost, no ledger row");
        (await _fixture.SuggestionCountAsync()).Should().Be(0, "no new entry once the daily target is reached with stand-down on");
        (await PausedAdvisoryCountAsync()).Should().Be(1, "the operator is told the co-pilot stood down, not left guessing");
        (await OutboxBodiesAsync()).Should().ContainMatch(
            "*target*", "the advisory quotes the stand-down reason — the daily profit target is reached");
    }

    // =============================================================================================================
    // 2. The issuance-count window (gh#745 blind spot 2) — keyed on today's Central day, under the R-20 owner filter.
    // =============================================================================================================

    [Fact]
    public async Task Throttle_ShouldCountIssuanceOnTodaysCentralDayOnly_NotYesterdays()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        // Loss of 900 against a 1,000 governor → headroom fraction 0.1 → Throttled with a per-window cap of 1.
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: -900m);

        // One suggestion already issued on this account YESTERDAY (04:00Z — after UTC midnight but before today's
        // 05:00Z Central open). It must NOT count against today's cap of 1: keyed on a UTC-date window it WOULD (04:00Z
        // buckets under 07-29), and today's fire would be wrongly dropped. A distinct instrument, so the supersede path
        // never touches it.
        await SeedSuggestionAsync(userId, accountId, mode, instrument: "NQ", _beforeCentralOpenUtc, confidence: 80);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        await _fixture.ScanAsync();

        // Today's window count is 0, so the cap of 1 admits this fire: the ES proposal is staged alongside yesterday's.
        (await _fixture.SuggestionCountAsync()).Should().Be(
            2, "yesterday's suggestion is outside today's Central window, so today's cap of 1 still admits this fire");
        (await SuggestionCountForInstrumentAsync("ES")).Should().Be(
            1, "the fresh proposal is on the fired instrument — yesterday's NQ row did not consume today's cap");
        _factory.Llm.CallCount.Should().Be(1, "Throttled still wakes the reviewer — 'fewer', enforced at staging, is not 'none'");
    }

    [Fact]
    public async Task Throttle_ShouldStopIssuing_OnceTodaysCountReachesThePerWindowCap()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: -900m); // fraction 0.1 → cap 1

        // One suggestion already issued on this account TODAY. With a throttled cap of 1, the window is already full,
        // so a fresh fire must be dropped at staging (the deterministic arm is binding). NQ so no supersede.
        await SeedSuggestionAsync(userId, accountId, mode, instrument: "NQ", _suggestionTodayUtc, confidence: 80);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        await _fixture.ScanAsync();

        // Were the count keyed on the wrong window it would read 0 and admit a second row — the safety invariant is
        // "reduce or suppress, never increase", so this must hold at 1.
        (await _fixture.SuggestionCountAsync()).Should().Be(
            1, "today's cap of 1 is already met, so the fresh fire issues nothing new");
        (await SuggestionCountForInstrumentAsync("ES")).Should().Be(0, "no proposal is staged on the fired instrument once the cap is met");
        _factory.Llm.CallCount.Should().Be(
            1, "the reviewer WAS woken and DID propose — the drop is the deterministic cap at staging, not suppression before the call");
    }

    [Fact]
    public async Task Throttle_ShouldCountOnlyTheOwnersOwnSuggestions_TowardTheCap()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: -900m); // fraction 0.1 → cap 1

        // A stranger's suggestion, stamped TODAY on the SAME account id but owned by a DIFFERENT operator. Production
        // cannot mint that row (an account belongs to one operator), which is exactly why the guard constructs it —
        // the throttle count runs in the owner-scoped context, so R-20 must exclude it. An unscoped count
        // (IgnoreQueryFilters) would see it, read the window as full, and drop the owner's own fire.
        await SeedSuggestionAsync(Guid.NewGuid(), accountId, mode, instrument: "NQ", _suggestionTodayUtc, confidence: 80);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        await _fixture.ScanAsync();

        (await SuggestionCountForInstrumentAsync("ES")).Should().Be(
            1, "the owner's own window is empty — a stranger's suggestion on the same account must not consume the operator's cap (R-20)");
    }

    // =============================================================================================================
    // 3. Two fires, one pass (gh#745 area 5) — the within-pass change-tracker tally, on real PG under one SaveChanges.
    // =============================================================================================================

    [Fact]
    public async Task Throttle_ShouldNotIssuePastTheCap_AcrossTwoAgentReviewFiresInOneScanPass()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: -900m); // fraction 0.1 → cap 1

        // TWO armed agent-review triggers on the one account, both crossing this pass. They are distinct rows (the
        // supersede lookup keys on trigger identity, so neither voids the other), so both wake the reviewer and both
        // reach staging within the SAME per-owner SaveChanges. A committed-only count would read 0 for BOTH and issue
        // two, blowing the cap of 1 in exactly the depleting-headroom regime the throttle exists for; the change-tracker
        // tally (the gh#455 shared-transaction pattern) is what makes the second fire see the first's pending row.
        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(2, "both armed edges fired this pass");
        _factory.Llm.CallCount.Should().Be(2, "each fire wakes the reviewer — the throttle caps issuance at staging, not the review itself");
        (await _fixture.SuggestionCountAsync()).Should().Be(
            1, "the cap of 1 holds ACROSS both fires in one pass — the within-pass tally stops the second slipping under a stale committed count");
    }

    // =============================================================================================================
    // 4. The conviction floor (gh#588) — "higher-conviction only" while throttled; confidence is a filter, never a lift.
    // =============================================================================================================

    [Fact]
    public async Task Throttle_ShouldDropACandidateBelowTheConvictionFloor_WhileThrottled()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();
        // Loss of 600 against a 1,000 governor → fraction 0.4 → Throttled with cap 2 (room to spare) and the
        // conviction floor of 70 active. So the drop below is unambiguously the FLOOR, not the cap.
        await DeclareRiskProfileAsync(accountId, governor: 1_000m);
        TradingMode mode = await _fixture.AccountModeAsync(accountId);
        await SeedClosedTradeAsync(userId, accountId, mode, _todayMidMorningUtc, realized: -600m);

        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);
        // A confidence of 50, below the 70 floor. Were the floor ignored (or confidence able to lift the cap rather
        // than only filter under it), this would stage a proposal; while throttled it must be dropped.
        _factory.Llm.Returns("""
            {"decision":"suggest","direction":"long","entry":5000,"stop":4990,"target":5020,"confidence":50,"reason":"low conviction"}
            """);

        await _fixture.ScanAsync();

        _factory.Llm.CallCount.Should().Be(1, "the reviewer was woken and proposed — the drop is the conviction floor at staging");
        (await _fixture.SuggestionCountAsync()).Should().Be(
            0, "while throttled, a below-floor candidate is dropped — 'higher-conviction only', and confidence can never lift the cap");
    }

    // =============================================================================================================
    // 5. Opt-in config round-trip (gh#745 area 4) — a nonsensical throttle knob fails the HOST at start, not each fire.
    // =============================================================================================================

    [Theory]
    [InlineData(nameof(SuggestionOptions.ThrottleThresholdFraction), "0")]
    [InlineData(nameof(SuggestionOptions.ThrottleThresholdFraction), "1.5")]
    [InlineData(nameof(SuggestionOptions.ThrottleFullWindowCap), "0")]
    [InlineData(nameof(SuggestionOptions.ThrottleConvictionFloor), "101")]
    public async Task Host_ShouldFailToStart_WhenAThrottleKnobIsOutOfRange(string knob, string badValue)
    {
        // The three throttle knobs feed SuggestionThrottlePolicy.Declare on every agent-review fire, which THROWS on
        // an out-of-range value. Program.cs validates them ValidateOnStart, so a bad configuration fails the host ONCE
        // rather than aborting an owner's scan pass per fire — the gap the unit tests cannot reach. This drives the
        // real host composition; only the one knob under test is out of range, so the refusal is deterministic.
        await using WebApplicationFactory<Program> badHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting($"{SuggestionOptions.SectionName}:{knob}", badValue));

        Func<Task> boot = async () =>
        {
            // Touching the pipeline forces the deferred host to build and start, running the startup validator.
            using HttpClient client = badHost.CreateClient();
        };

        (await boot.Should().ThrowAsync<OptionsValidationException>(
            "an out-of-range throttle knob must fail the host at start, not silently disable the throttle or throw per fire"))
            .Which.Message.Should().Contain(
                "Suggestions", "the failure must be the Suggestions options validation, not an unrelated startup fault");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private async Task ResetAsync()
    {
        _factory.Llm.Reset();
        _factory.Pushover.Reset();
        await _factory.ClearOutboxAsync();
        await ClearAiUsageAsync();
        // ClearAsync deletes Accounts (cascading Trades, RiskProfiles and agent-review triggers) plus Suggestions,
        // firings, triggers and indicator rows — so trade and profile state never leaks between cases.
        await _fixture.ClearAsync();
    }

    /// <summary>Declares the account's risk rules over HTTP — every layer other than the governor / target is generous,
    /// so the throttle regime is driven purely by the seeded realized P&amp;L, not a competing limit.</summary>
    private async Task DeclareRiskProfileAsync(
        Guid accountId, decimal governor, decimal? dailyProfitTarget = null, bool standDown = false)
    {
        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage risk = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 5_000m,
                AccountProfitTarget: 10_000m,
                StartingBalance: 50_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: null,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 1_000m,
                DailyDrawdownGovernor: governor,
                DailyProfitTarget: dailyProfitTarget,
                StopForDayAtProfitTarget: standDown,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: null));
        risk.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        login.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await login.Content.ReadFromJsonAsync<LoginTokenResponse>(_json);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Writes one closed trade straight at the context (the throttle read touches no venue). <paramref name="mode"/>
    /// is the account's current mode, so the R-14-filtered reader (gh#746) counts it; the timestamp is UTC because Npgsql
    /// refuses a non-zero offset for a <c>timestamptz</c> column.</summary>
    private Task SeedClosedTradeAsync(
        Guid userId, Guid accountId, TradingMode mode, DateTimeOffset closedAtUtc, decimal realized) =>
        _factory.WithDatabaseAsync(async database =>
        {
            database.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.M26",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                ExitPrice = 5_010m,
                RealizedPnL = realized,
                Mode = mode,
                ClosedAt = closedAtUtc,
            });
            await database.SaveChangesAsync();
            return 0;
        });

    /// <summary>Seeds one already-issued suggestion for the throttle's window count. <paramref name="mode"/> must equal the
    /// account's persisted mode or the R-14 DB trigger refuses the row.</summary>
    private Task SeedSuggestionAsync(
        Guid userId, Guid accountId, TradingMode mode, string instrument, DateTimeOffset createdAtUtc, int confidence) =>
        _factory.WithDatabaseAsync(async database =>
        {
            database.Suggestions.Add(new Suggestion
            {
                Origin = SuggestionOrigin.Scan,
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                Instrument = instrument,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                StopPrice = 4_990m,
                TargetPrice = 5_020m,
                Mode = mode,
                State = SuggestionState.Active,
                CreatedAt = createdAtUtc,
                Rationale = "seed",
                CitedFactors =
                [
                    new CitedFactor
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        Kind = CitedFactorKind.Indicator,
                        IsPrimary = true,
                        TimeframeMinutes = 5,
                        Indicator = "rsi",
                        Period = 14,
                    },
                ],
                Confidence = confidence,
                ExpiresAt = createdAtUtc.AddHours(1),
            });
            await database.SaveChangesAsync();
            return 0;
        });

    private Task<int> SuggestionCountForInstrumentAsync(string instrument) =>
        _factory.WithDatabaseAsync(database =>
            database.Suggestions.IgnoreQueryFilters().CountAsync(suggestion => suggestion.Instrument == instrument));

    private Task<int> AiUsageCountAsync() =>
        _factory.WithDatabaseAsync(database => database.AiUsage.IgnoreQueryFilters().CountAsync());

    private Task ClearAiUsageAsync() =>
        _factory.WithDatabaseAsync(async database =>
        {
            await database.AiUsage.IgnoreQueryFilters().ExecuteDeleteAsync();
            return 0;
        });

    // The advisory becomes durable in the outbox synchronously during the scan (gh#437), before any relay — so "the
    // operator was advised" is asserted there, exactly as the AI-spend suite asserts its budget advisory.
    private Task<int> PausedAdvisoryCountAsync() =>
        _factory.WithDatabaseAsync(database =>
            database.NotificationOutbox.CountAsync(row => row.Title.StartsWith("Suggestions paused")));

    private Task<List<string>> OutboxBodiesAsync() =>
        _factory.WithDatabaseAsync(database => database.NotificationOutbox.Select(row => row.Body).ToListAsync());
}
