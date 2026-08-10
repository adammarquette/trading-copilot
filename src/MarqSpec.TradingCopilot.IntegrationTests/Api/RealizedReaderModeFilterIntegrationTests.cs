using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The <b>R-14 mode filter on the two realized-P&amp;L readers</b> end to end on real Postgres (gh#756, of gh#746;
/// R-14, R-5, R-4, R-20, ADR-0007). <c>DailyRealizedReader</c> and <c>ConsistencyWindowReader</c> must count only
/// trades taken under the account's <b>current</b> mode, so a practice result can never feed a live account's
/// governor, headroom or consistency window — "practice results never blend into live results", enforced one layer
/// above the gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the spec</b> (gh#756's acceptance list, gh#746's statement of the gap, R-14), independently of
/// the fix. The unit tier already covers both readers on the EF in-memory provider. What it cannot cover is what
/// this suite exists for: the account-mode read, the R-20 default-deny filter over it, the <c>Trade.Mode</c>
/// residual against the real <c>(AccountId, ClosedAt)</c> index, the <c>CK_Trades_Mode_NotUndeclared</c> check
/// constraint, and the real FK between a risk profile and its account — none of which exist on an in-memory
/// provider. Every case below is an account with trades on <b>both sides of a mode change</b>, which gh#746 names
/// as the case that matters.
/// </para>
/// <para>
/// <b>Everything here is driven over HTTP, deliberately.</b> The fix changes both readers' <i>signatures</i>
/// (they gain a <c>TradingMode</c> parameter), so a suite that called them directly would not compile against a
/// base that lacks the fix — and a QA project that does not compile takes every other suite down with it. Driving
/// the endpoints instead means this file compiles and runs identically before and after gh#746's fix lands, and
/// reports the behaviour difference as a test result rather than as a build break.
/// </para>
/// <para>
/// <b>EXPECTED RED ON THIS BASE — do not file it as a new defect.</b> The fix is
/// <a href="https://github.com/adammarquette/trading-copilot/pull/755">PR #755</a> (<c>Closes #746</c>), still
/// <b>open</b> at the time of writing; this branch is cut from <c>develop</c> per the branching rule, so it runs
/// against readers that have <b>no</b> mode filter at all. That is exactly the state gh#756's "prove-the-red"
/// note asks for — "revert the <c>Trade.Mode == mode</c> filter locally and witness a practice trade leaking into
/// a live limit" — reached by <i>base</i> rather than by local edit, and it is therefore whole rather than
/// partial: nothing needed reverting because nothing is there yet. Six of the eight cases below are expected to
/// fail on this base and to go green when #755 merges, at which point they become that fix's regression guards:
/// <list type="bullet">
/// <item><description><b>Expected RED until #755 merges</b> — the six that assert the filter:
/// <see cref="SendPath_ShouldNotDisqualifyALivePayout_WithAPracticeDayFromBeforeTheModeChange"/>,
/// <see cref="SendPath_ShouldNotRescueALiveConsistencyBreach_WithPracticeDaysFromBeforeTheModeChange"/>,
/// <see cref="Headroom_ShouldCountOnlyTheLiveLoss_OnAModeChangedAccount"/>,
/// <see cref="Headroom_ShouldNotBeInflatedByAPracticeProfit_OnAModeChangedAccount"/>,
/// <see cref="Headroom_ShouldCountNothing_ForAnUndeclaredAccountWithPracticeTradesToday"/>, and
/// <see cref="Headroom_ShouldNeverServeAnotherOperatorsAccountMode_WhenTheProfileIsVisibleButTheAccountIsNot"/>
/// (which fails for a second reason on this base: with no account-mode read at all, the endpoint never consults
/// the account, so it answers for one it does not own).</description></item>
/// <item><description><b>Expected GREEN on both sides</b> — the two structural guards, which are about the
/// database rather than the filter: <see cref="TheDatabase_ShouldRefuseATrade_WhoseModeIsUndeclared"/> (the check
/// constraint is what makes "an undeclared account has no countable trades" true <i>by construction</i>) and
/// <see cref="Headroom_ShouldReturn404_WhenTheAccountRowIsGone_AndTheProfileCannotOutliveIt"/> (the real FK is
/// what makes the fix's defensive <c>mode is null</c> branch unreachable). Both can still fail — flipping the
/// check constraint or the FK's delete behaviour reddens them — they simply do not depend on gh#746.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Honest note on how the red was witnessed.</b> The authoring sandbox has no Docker daemon
/// (<c>/var/run/docker.sock</c> absent), so the container tier could not be run locally at all and no assertion
/// below has been observed to fail on this machine. The claim above is derived from source: on this base
/// <c>DailyRealizedReader</c> / <c>ConsistencyWindowReader</c> filter on <c>AccountId</c>, <c>ClosedAt</c> and
/// <c>RealizedPnL</c> only, and <c>RiskEndpoints.GetHeadroomAsync</c> reads no account mode — so a practice row
/// on a now-live account is counted, which is what each expected-red case asserts against. The
/// <c>integration tests (pre-merge)</c> job on this suite's PR is the authoritative signal; re-run it after #755
/// merges and the six should flip to green with no edit to this file.
/// </para>
/// <para>
/// <b>Tier:</b> pre-merge, container-backed Postgres (<see cref="StubbedVenuePostgresFactory"/>, real migrations).
/// The venue seam is the one sanctioned double and stays adversarial: it feeds account rosters, balances and
/// fills, and never the realized-P&amp;L figures or the consistency fraction under test — every trade this suite
/// measures is a <c>Trade</c> row it wrote itself. The two send-path cases run against a host booted as
/// <c>Production</c>, because a <c>Live</c> account is refused everywhere else by the R-14 environment gate and
/// the mode change gh#746 is about only reaches its interesting end when the account can actually trade.
/// </para>
/// <para>
/// <b>No production code is touched by this PR</b> (QA contract, guard discipline rule 3): the red belongs to
/// gh#746, whose fix is already in flight as #755, so there is nothing here to file and nothing here to fix.
/// </para>
/// </remarks>
public class RealizedReaderModeFilterIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public RealizedReaderModeFilterIntegrationTests(StubbedVenuePostgresFactory factory) => _factory = factory;

    // -----------------------------------------------------------------------------------------------------------
    // 1. The send-path consistency gate (gh#756 scope 1) — a practice day can neither disqualify nor rescue a live
    //    payout it was never part of. Both cases are constructed so the blended and the mode-filtered windows reach
    //    OPPOSITE verdicts: a case where both agree would pass with and against the defect.
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendPath_ShouldNotDisqualifyALivePayout_WithAPracticeDayFromBeforeTheModeChange()
    {
        // The account traded Practice, then was reclassified Live. Its live record is two even days -- 500 and
        // 500, best day 50% of 1000, comfortably inside a 60% target. Its practice record is one outsized 2000 day
        // that belongs to an evaluation the live payout has nothing to do with.
        //
        //   live only (correct):  best 500  / total 1000 = 50.0%  <= 60%  -> nothing binds, the order goes
        //   blended  (gh#746):    best 2000 / total 3000 = 66.7%  >  60%  -> REFUSED, on a day that never happened
        //                                                                     under this account's live rules
        using WebApplicationFactory<Program> productionHost = _factory.CreateHost(DeploymentEnvironment.Production);
        HttpClient client = await AuthenticatedClientAsync(productionHost);
        Guid accountId = await SetupPracticeAccountAsync(client);

        await SeedClosedTradeAsync(accountId, Utc(7, 20, 15), 2_000m, TradingMode.Practice);

        await ChangeModeToLiveAsync(client, accountId);

        await SeedClosedTradeAsync(accountId, Utc(7, 22, 15), 500m, TradingMode.Live);
        await SeedClosedTradeAsync(accountId, Utc(7, 23, 15), 500m, TradingMode.Live);

        await DeclareRiskProfileAsync(client, accountId, maxBestDayFraction: 0.60m, enforcement: ConsistencyEnforcement.Block);

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        SendOrderResponse? decision = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the consistency window is a PER-MODE measure (R-14): a 2000 practice day was never part of this "
            + "account's live profit, so it must not disqualify a live payout the operator is compliant on");
        decision.Should().NotBeNull();
        decision!.BindingLayer.Should().NotBe(
            RiskLayer.ConsistencyTarget, "the live-only window is 50% against a 60% target — nothing there binds");
    }

    [Fact]
    public async Task SendPath_ShouldNotRescueALiveConsistencyBreach_WithPracticeDaysFromBeforeTheModeChange()
    {
        // The mirror image, and the dangerous direction: blending must not make a REAL breach look compliant. The
        // live record alone is 900 + 100 -- one day carrying 90% of the profit, far past a 50% target. Five even
        // practice days of 400 inflate the denominator without moving the best day, so the blended fraction drops
        // under the target and the order trades freely on an evaluation that is already disqualified.
        //
        //   live only (correct):  best 900 / total 1000 = 90%  >  50%  -> REFUSED, correctly
        //   blended  (gh#746):    best 900 / total 3000 = 30%  <= 50%  -> allowed, a breach reading as compliant
        using WebApplicationFactory<Program> productionHost = _factory.CreateHost(DeploymentEnvironment.Production);
        HttpClient client = await AuthenticatedClientAsync(productionHost);
        Guid accountId = await SetupPracticeAccountAsync(client);

        int[] practiceDays = [10, 11, 12, 13, 14];
        foreach (int day in practiceDays)
        {
            await SeedClosedTradeAsync(accountId, Utc(7, day, 15), 400m, TradingMode.Practice);
        }

        await ChangeModeToLiveAsync(client, accountId);

        await SeedClosedTradeAsync(accountId, Utc(7, 20, 15), 900m, TradingMode.Live);
        await SeedClosedTradeAsync(accountId, Utc(7, 22, 15), 100m, TradingMode.Live);

        await DeclareRiskProfileAsync(client, accountId, maxBestDayFraction: 0.50m, enforcement: ConsistencyEnforcement.Block);

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        SendOrderResponse? decision = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            "practice days must not pad the live denominator — a 90% live best day breaches a 50% target, and "
            + "blending would let a disqualified evaluation keep trading");
        decision.Should().NotBeNull();
        decision!.ApprovedQuantity.Should().Be(0, "a refusal approves nothing — a partial fill would still trade");
        decision.Reason.Should().Contain("consistency target", "the operator must be told which rule bound");
    }

    // -----------------------------------------------------------------------------------------------------------
    // 2. The headroom read (gh#756 scope 2) — GET /accounts/{id}/risk/headroom on a mode-changed account with a
    //    loss on BOTH sides of the change, closed today.
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Headroom_ShouldCountOnlyTheLiveLoss_OnAModeChangedAccount()
    {
        // Both losses close TODAY (Central), on the same account, differing only in Mode -- so the day boundary,
        // the owner filter and the realized-only rule are all held constant and Mode is the only variable left.
        // A practice loss eating live headroom would throttle suggestions (R-4) and shrink the governor (R-5) over
        // money that was never at risk.
        HttpClient client = await AuthenticatedClientAsync(_factory);
        Guid accountId = await SetupPracticeAccountAsync(client);

        await SeedClosedTradeAsync(accountId, ClosedTodayCentral(), -700m, TradingMode.Practice);

        await ChangeModeToLiveAsync(client, accountId);

        await SeedClosedTradeAsync(accountId, ClosedTodayCentral(), -200m, TradingMode.Live);

        await DeclareRiskProfileAsync(client, accountId, dailyDrawdownGovernor: 1_000m);

        DailyHeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(
            200m, "only the live loss belongs to a live account's day — the practice loss predates the mode change");
        headroom.UnderGovernor.Should().Be(
            800m, "a practice loss must not eat live headroom; blended, this would read 100 and throttle the operator");
        headroom.GovernorSpent.Should().BeFalse();
    }

    [Fact]
    public async Task Headroom_ShouldNotBeInflatedByAPracticeProfit_OnAModeChangedAccount()
    {
        // The unsafe direction, and the reason this filter is safety-critical rather than merely correct: a large
        // PRACTICE profit netted against a real live loss would report FULL headroom on an account that is 900
        // down. The reader sums a signed figure, so blending here does not merely mis-measure -- it hides a live
        // loss behind simulated money and hands the gate a governor that has not been spent.
        HttpClient client = await AuthenticatedClientAsync(_factory);
        Guid accountId = await SetupPracticeAccountAsync(client);

        await SeedClosedTradeAsync(accountId, ClosedTodayCentral(), 5_000m, TradingMode.Practice);

        await ChangeModeToLiveAsync(client, accountId);

        await SeedClosedTradeAsync(accountId, ClosedTodayCentral(), -900m, TradingMode.Live);

        await DeclareRiskProfileAsync(client, accountId, dailyDrawdownGovernor: 1_000m);

        DailyHeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(
            900m, "the live loss is the account's real day; a practice profit does not undo it");
        headroom.DayRealizedProfit.Should().Be(
            0m, "a losing live day has no realized profit — the 5000 was simulated and belongs to no live figure");
        headroom.UnderGovernor.Should().Be(
            100m, "blended this reads +4100 realized, reports a FULL 1000 governor, and lets the operator keep "
            + "trading a day that is already 900 down");
    }

    // -----------------------------------------------------------------------------------------------------------
    // 3. R-20 (gh#756 scope 3) — the account-mode read is default-deny scoped exactly like the Trade read.
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Headroom_ShouldNeverServeAnotherOperatorsAccountMode_WhenTheProfileIsVisibleButTheAccountIsNot()
    {
        // The mode read is a NEW query over a second table, and a query written with IgnoreQueryFilters would leak
        // across tenants while every mode-filter test above still passed. Isolating it needs the one shape where
        // the profile read cannot answer first: a profile visible to A whose account belongs to B. Production
        // cannot produce that row -- which is precisely why the guard has to construct it, or a default-deny miss
        // on the account read is invisible from outside.
        HttpClient operatorA = await AuthenticatedClientAsync(_factory);
        HttpClient operatorB = await CreateSecondOperatorAsync(operatorA);

        Guid accountOfB = await SetupPracticeAccountAsync(operatorB);
        await ChangeModeToLiveAsync(operatorB, accountOfB);
        await DeclareRiskProfileAsync(operatorB, accountOfB, dailyDrawdownGovernor: 1_000m);

        using (HttpResponseMessage strangerRead = await operatorA.GetAsync($"/accounts/{accountOfB}/risk/headroom"))
        {
            strangerRead.StatusCode.Should().Be(
                HttpStatusCode.NotFound, "the ordinary cross-tenant read is refused at the profile — 404, never a figure");
        }

        // Now hand A the profile row while the ACCOUNT stays B's, so the only thing standing between A and B's
        // account mode is the filter on the account read itself.
        Guid operatorAId = await UserIdAsync(PostgresApiFactory.OperatorEmail);
        await ExecuteDbAsync(async database =>
        {
            RiskProfileRecord profile = await database.RiskProfiles
                .IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.AccountId == accountOfB);
            profile.UserId = operatorAId;
            await database.SaveChangesAsync();
        });

        using HttpResponseMessage response = await operatorA.GetAsync($"/accounts/{accountOfB}/risk/headroom");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the account-mode read must be default-deny scoped like the Trade read (R-20): another operator's "
            + "account must not resolve a mode, and an unscoped read would answer with B's account behind it");
    }

    // -----------------------------------------------------------------------------------------------------------
    // 4. Undeclared and vanished (gh#756 scope 4) — the two absences, against a real check constraint and a real FK.
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Headroom_ShouldCountNothing_ForAnUndeclaredAccountWithPracticeTradesToday()
    {
        // An account whose effective stage the firm has not classified is Undeclared -- refused everywhere,
        // production included. Its journal still holds the practice trades it took before the reclassification, and
        // no trade can ever carry Undeclared (see the check-constraint guard below), so the honest answer is the
        // EMPTY one: zero consumed, full headroom. Blended, the leftover practice loss would spend the governor of
        // an account that is not permitted to produce a single order.
        HttpClient client = await AuthenticatedClientAsync(_factory);
        Guid accountId = await SetupPracticeAccountAsync(client);

        await SeedClosedTradeAsync(accountId, ClosedTodayCentral(), -700m, TradingMode.Practice);
        await DeclareRiskProfileAsync(client, accountId, dailyDrawdownGovernor: 1_000m);

        // Funded is deliberately NOT among the firm's declared conventions (see SetupPracticeAccountAsync), so the
        // override moves the effective stage to something the operator has never classified: mode -> Undeclared.
        AccountResponse account = await SetStageAsync(client, accountId, AccountStage.Funded);
        account.Mode.Should().Be(
            TradingMode.Undeclared, "an unclassified effective stage resolves to Undeclared — silence is not consent");

        DailyHeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(
            0m, "no trade can be taken under Undeclared, so an undeclared account has no countable day");
        headroom.UnderGovernor.Should().Be(
            1_000m, "the leftover practice loss belongs to the mode it was taken under, not to this one");
    }

    [Fact]
    public async Task TheDatabase_ShouldRefuseATrade_WhoseModeIsUndeclared()
    {
        // What makes "an undeclared account has no countable trades" true BY CONSTRUCTION rather than by luck:
        // CK_Trades_Mode_NotUndeclared, live only against real Postgres (an in-memory provider enforces no check
        // constraint). Without it the case above would rest on an assumption no layer actually holds.
        HttpClient client = await AuthenticatedClientAsync(_factory);
        Guid accountId = await SetupPracticeAccountAsync(client);
        Guid ownerId = await OperatorIdForAccountAsync(accountId);

        Func<Task> insert = () => ExecuteDbAsync(async database =>
        {
            database.Trades.Add(NewTrade(accountId, ownerId, ClosedTodayCentral(), -100m, TradingMode.Undeclared));
            await database.SaveChangesAsync();
        });

        await insert.Should().ThrowAsync<DbUpdateException>(
            "the journal's check constraint refuses an Undeclared trade outright — 'practice never blends into "
            + "live' starts by making the third mode unwritable");
    }

    [Fact]
    public async Task Headroom_ShouldReturn404_WhenTheAccountRowIsGone_AndTheProfileCannotOutliveIt()
    {
        // The fix guards a "the account vanished between the profile read and the mode read" race with a 404. On a
        // real FK that orphan cannot be constructed at all: RiskProfiles -> Accounts is ON DELETE CASCADE, so
        // deleting the account takes the profile with it and the read 404s one branch earlier. Asserting both halves
        // records WHY the defensive branch is unreachable rather than leaving it looking untested — and it still
        // fails on its own subject: change that FK to SetNull / Restrict and this reddens.
        HttpClient client = await AuthenticatedClientAsync(_factory);
        Guid accountId = await SetupPracticeAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId, dailyDrawdownGovernor: 1_000m);

        await ExecuteDbAsync(async database =>
        {
            Account account = await database.Accounts
                .IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == accountId);
            database.Accounts.Remove(account);
            await database.SaveChangesAsync();
        });

        int survivingProfiles = await QueryDbAsync(database => database.RiskProfiles
            .IgnoreQueryFilters()
            .CountAsync(profile => profile.AccountId == accountId));
        survivingProfiles.Should().Be(
            0, "a risk profile cannot outlive its account — the FK cascades, so the orphan the mode-is-null branch "
            + "guards against cannot exist on real Postgres");

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk/headroom");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "a vanished account has no headroom to project — fail closed, never a figure");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Helpers — the harness pattern of ConsistencyTargetIntegrationTests / DailyHeadroomReadIntegrationTests.
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>An instant in 2026 given as UTC; 15:00 UTC is mid-morning Central, well clear of either midnight.</summary>
    private static DateTimeOffset Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>An instant safely inside TODAY's Central trading day, computed from the real Central "now" the
    /// suite runs under — so the day-scoped cases hold regardless of run date or daylight saving.</summary>
    private static DateTimeOffset ClosedTodayCentral()
    {
        DateTimeOffset centralNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MarketClock.CentralTime);
        DateTime centralMidnight = centralNow.Date;
        TimeSpan offset = MarketClock.CentralTime.GetUtcOffset(centralMidnight);
        return new DateTimeOffset(centralMidnight, offset).AddHours(10);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> host)
    {
        HttpClient client = host.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<HttpClient> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"operator-b-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OperatorB-Pass123!", "Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    /// <summary>
    /// A discovered account explicitly declared <b>Practice</b> — the "before" side of every mode change here.
    /// The firm classifies Practice and Evaluation (capital not at risk) and Funded (capital at risk); the stage
    /// override is set rather than inferred so the starting mode is the test's, not the name-resolver's.
    /// <b>No convention is declared for <see cref="AccountStage.Unknown"/></b> — undeclarable — and none for any
    /// stage beyond those three, which is what the undeclared case leans on.
    /// </summary>
    private async Task<Guid> SetupPracticeAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-ModeFilter-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true),
            ]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        Guid accountId = accounts.First(account => account.CanTrade).Id;

        AccountResponse practice = await SetStageAsync(client, accountId, AccountStage.Practice);
        practice.Mode.Should().Be(TradingMode.Practice, "the 'before' side of the mode change must be Practice");
        return accountId;
    }

    /// <summary>
    /// The mode change itself, made the way an operator makes one: reclassify the account's stage to one the firm
    /// has declared as capital-at-risk. The journal is untouched by it — <c>Trade.Mode</c> is placement-time truth
    /// and is never rewritten, which is exactly why the readers have to filter.
    /// </summary>
    private async Task ChangeModeToLiveAsync(HttpClient client, Guid accountId)
    {
        AccountResponse account = await SetStageAsync(client, accountId, AccountStage.Funded);
        account.Mode.Should().Be(
            TradingMode.Live, "the account must actually be Live afterwards, or the case under test never happened");
    }

    private async Task<AccountResponse> SetStageAsync(HttpClient client, Guid accountId, AccountStage stage)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/stage", new SetStageOverrideRequest(stage));
        response.EnsureSuccessStatusCode();
        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(account);
        return account;
    }

    /// <summary>
    /// Declares the account's risk rules. Every layer other than the one under test is deliberately generous, so
    /// that when an order is refused the consistency target is unambiguously the layer that bound, and the daily
    /// figures come from the reader rather than from a competing limit.
    /// </summary>
    private async Task DeclareRiskProfileAsync(
        HttpClient client,
        Guid accountId,
        decimal dailyDrawdownGovernor = 2_000m,
        decimal? maxBestDayFraction = null,
        ConsistencyEnforcement enforcement = ConsistencyEnforcement.Advisory)
    {
        using HttpResponseMessage risk = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 5_000m,
                AccountProfitTarget: 10_000m,
                StartingBalance: 50_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: 50_100m,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 1_000m,
                DailyDrawdownGovernor: dailyDrawdownGovernor,
                DailyProfitTarget: null,
                StopForDayAtProfitTarget: false,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: maxBestDayFraction,
                ConsistencyEnforcement: enforcement));
        risk.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> SendOrderAsync(HttpClient client, Guid accountId) =>
        client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/",
            new SendOrderRequest(
                Symbol: "MES",
                TickSize: 0.25m,
                PointValue: 5m,
                Side: OrderSide.Buy,
                Quantity: 1,
                Entry: 5_000m,
                Stop: 4_990m,
                SafetyStop: 4_980m,
                ReferencePrice: 5_000m,
                Type: OrderType.Market));

    private async Task<DailyHeadroomResponse> GetHeadroomAsync(HttpClient client, Guid accountId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk/headroom");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        DailyHeadroomResponse? headroom = await response.Content.ReadFromJsonAsync<DailyHeadroomResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(headroom);
        return headroom;
    }

    /// <summary>
    /// Writes one closed trade straight at the context (this read touches no venue, and the venue double must never
    /// supply a figure the system is meant to compute). <paramref name="mode"/> is placement-time truth: the DB's
    /// mode trigger guards Orders and Suggestions, deliberately <b>not</b> Trades, precisely because a trade closes
    /// after placement — when the account's declaration may legitimately have moved on.
    /// </summary>
    private async Task SeedClosedTradeAsync(Guid accountId, DateTimeOffset closedAt, decimal realized, TradingMode mode)
    {
        Guid ownerId = await OperatorIdForAccountAsync(accountId);
        await ExecuteDbAsync(async database =>
        {
            database.Trades.Add(NewTrade(accountId, ownerId, closedAt, realized, mode));
            await database.SaveChangesAsync();
        });
    }

    private static Trade NewTrade(Guid accountId, Guid userId, DateTimeOffset closedAt, decimal realized, TradingMode mode) => new()
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
        ClosedAt = closedAt,
    };

    /// <summary>The OWNING operator of an account — accounts created by the second operator belong to them, not to
    /// the bootstrap operator, and a trade seeded under the wrong owner would be invisible to the read (R-20).</summary>
    private Task<Guid> OperatorIdForAccountAsync(Guid accountId) => QueryDbAsync(database => database.Accounts
        .IgnoreQueryFilters()
        .Where(account => account.Id == accountId)
        .Select(account => account.UserId)
        .SingleAsync());

    private Task<Guid> UserIdAsync(string email) => QueryDbAsync(database => database.Users
        .Where(user => user.Email == email)
        .Select(user => user.Id)
        .SingleAsync());

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
