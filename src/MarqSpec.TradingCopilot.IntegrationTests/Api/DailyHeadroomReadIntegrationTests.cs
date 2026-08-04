using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the daily-headroom read surface (<c>GET /accounts/{id}/risk/headroom</c> +
/// <c>DailyRealizedReader</c>) specified by gh#587 / gh#638 (R-5) — the projection that lets the proactive,
/// suggestion-time governor (gh#551) and any future headroom display (U3 gh#25) read today's consumed headroom
/// without composing an order.
/// </summary>
/// <remarks>
/// <para>
/// <b>BLOCKED — filed as gh#640 — do not un-skip without reading it first.</b> Written from the spec (gh#638,
/// gh#587, R-5), independently of any implementation, per the QA contract. Reconnaissance before writing this
/// suite (mandatory per the QA contract's independence rule — confirming the route/types, not copying logic)
/// found that <b>neither the endpoint nor the reader exist on <c>develop</c></b>:
/// <list type="bullet">
/// <item><description><c>Api/Risk/RiskEndpoints.cs</c> maps only <c>PUT /accounts/{id}/risk</c> and
/// <c>GET /accounts/{id}/risk</c> (the declared profile) — no <c>/headroom</c> route.</description></item>
/// <item><description>No <c>DailyRealizedReader</c> type exists anywhere in the solution (contrast
/// <c>Data/ConsistencyWindowReader.cs</c>, the gh#380 sibling this issue traces to, which reads a related but
/// different projection — total/best-day profit, not today's Central-day loss).</description></item>
/// <item><description>gh#587 (the parent coding task) is still <b>open</b>: its body carries an explicit
/// "OPEN DESIGN QUESTION — why this is Planning, not ToDo" about what mechanism would update the projection,
/// and says outright "Recommend settling it, then this drops to ToDo." Only its pure-arithmetic half shipped,
/// split out as gh#627 (<see cref="DailyHeadroom"/>, closed) — the persistence/read half never did.</description></item>
/// <item><description>The gate's own send-time day-loss input is itself currently hardcoded
/// (<c>Api/Orders/OrderEndpoints.cs</c>: <c>new AccountRiskState(venueAccount.Balance, UnrealizedPnL: 0m,
/// DayRealizedPnL: 0m)</c>) — noted here as directly relevant context for whoever picks up gh#640, not asserted
/// as part of this suite's scope.</description></item>
/// </list>
/// </para>
/// <para>
/// Per the QA contract's guard discipline, rule 3: <i>"a suite that cannot go green without a production change
/// has found a defect — report it, don't fix it."</i> Every scope bullet in gh#638 requires the missing HTTP
/// route, so there is no partial slice of this card that is unblocked. The correct deliverable is this suite,
/// fully written to the spec and ready to run the moment gh#640 ships, with every test <c>Skip</c>-annotated
/// rather than left silently green or silently red.
/// </para>
/// <para>
/// <b>"Prove the red" for this suite, honestly stated:</b> the blocking defect itself — the absent route and
/// reader — is established directly by source inspection (above), not by a live HTTP 404, because this
/// development sandbox's egress policy separately blocks Docker Hub's CDN
/// (<c>production.cloudfront.docker.com</c> / <c>pkg-containers.githubusercontent.com</c>, both <c>403</c> from
/// the pre-configured proxy), so the container tier's Postgres/Ryuk images could not be pulled locally at all.
/// Each test below <b>was</b> run unskipped against this branch and did fail — every one at the identical
/// infrastructure step (the shared <see cref="StubbedVenuePostgresFactory"/> fixture's container failing to
/// start), confirming the tests are not independently broken and the suite is wired correctly end to end up to
/// that point — but that is a sandbox-network red, not the endpoint's own 404. Do not read the comment on any
/// individual test as a claim that its specific HTTP assertion was observed to fail; only source inspection
/// backs the "the route does not exist" claim. CI's own network context is expected to pull these images
/// normally, and the `integration tests (pre-merge)` check on this suite's PR is the authoritative signal for
/// what actually happens once un-skipped.
/// </para>
/// <para>
/// <b>Tier:</b> pre-merge, container-backed Postgres (<see cref="StubbedVenuePostgresFactory"/>). Venue-independent
/// — this read touches no venue, so no venue double is needed (gh#638's own scope note).
/// </para>
/// </remarks>
public class DailyHeadroomReadIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private const string BlockedBy = "blocked by gh#640 — GET /accounts/{id}/risk/headroom + DailyRealizedReader "
        + "(gh#587) do not exist yet; gh#587 is still open, blocked on an unresolved persistence-mechanism decision";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    /// <summary>
    /// The read surface's response shape, as specified by gh#638 — not a production type (none exists), so this
    /// is a local, test-owned contract the suite deserializes against. Ready to swap for the real DTO once gh#640
    /// ships one.
    /// </summary>
    private sealed record HeadroomResponse(
        decimal? UnderDailyLossLimit,
        decimal UnderGovernor,
        bool DailyLossLimitSpent,
        bool GovernorSpent,
        decimal DayLoss,
        decimal DayRealizedProfit,
        bool DailyTargetReached);

    public DailyHeadroomReadIntegrationTests(StubbedVenuePostgresFactory factory) => _factory = factory;

    // -------------------------------------------------------------------------------------------------------
    // Against the gate, end-to-end (R-5) — the read must be the gate's arithmetic, not a second definition.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldEqualTheGatesArithmetic_ForAKnownDayLoss()
    {
        // Guard-discipline "prove red by perturbing one input": swap either seeded loss below for a different
        // figure (or the declared governor/limit) and the `expected` and the response diverge — this assertion
        // is not satisfiable by a hardcoded or copy-pasted response value.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 1_000m, dailyDrawdownGovernor: 800m);
        await SeedClosedTradesTodayAsync(accountId, -300m, -150m); // day loss = 450

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        DailyHeadroom expected = DailyHeadroom.Remaining(dailyLossLimit: 1_000m, dailyDrawdownGovernor: 800m, dayLoss: 450m);
        headroom.UnderDailyLossLimit.Should().Be(
            expected.UnderDailyLossLimit, "the read must be the SAME arithmetic the gate applies (R-5), not a second definition");
        headroom.UnderGovernor.Should().Be(expected.UnderGovernor);
    }

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldLeaveTheHardLimitAbsent_WhenTheFirmImposesNone()
    {
        // Mirrors DailyHeadroomTests.Remaining_ShouldLeaveTheHardLimitAbsent_WhenTheFirmImposesNone (gh#627) at
        // the HTTP boundary — absence must round-trip as null, never a fabricated permissive default (R-5).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: null, dailyDrawdownGovernor: 800m);
        await SeedClosedTradesTodayAsync(accountId, -300m);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.UnderDailyLossLimit.Should().BeNull("a firm that imposes no hard daily limit must read as absent, not zero or unlimited");
        headroom.UnderGovernor.Should().Be(500m);
    }

    // -------------------------------------------------------------------------------------------------------
    // Central-day rollover on real Postgres — the timestamptz round-trip + MarketClock conversion.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldExcludeATradeClosedJustBeforeCentralMidnight_AndIncludeOneJustAfter()
    {
        // A trade one minute before Central midnight is YESTERDAY's Central trading day; one minute after is
        // TODAY's. A UTC-date bucketing would draw this line at a different instant (US Central is never UTC),
        // splitting or shifting a live CME session (the auto-flatten's own boundary). Both instants are computed
        // relative to the ACTUAL Central "now" the suite runs under, so this holds regardless of run date or DST.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 2_000m);

        DateTimeOffset centralMidnightToday = CentralMidnightToday();
        await SeedClosedTradeAsync(accountId, centralMidnightToday.AddMinutes(-1), realized: -900m); // yesterday, Central
        await SeedClosedTradeAsync(accountId, centralMidnightToday.AddMinutes(1), realized: -200m);   // today, Central

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(200m, "only the trade closed AFTER Central midnight belongs to today's trading day");
        headroom.UnderGovernor.Should().Be(1_800m, "yesterday's -900 must not spend today's governor");
    }

    // -------------------------------------------------------------------------------------------------------
    // Two distinct absences — never conflated.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldReturn404_ForAnUndeclaredAccount()
    {
        // Fail-closed, the same convention GetRiskAsync already uses: an undeclared account has no risk profile
        // to project headroom FROM, so absence of the declaration reads as 404 — never a fabricated "full" headroom.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupUndeclaredAccountAsync(client);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk/headroom");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "an account with no declared risk profile has no headroom to read");
    }

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldBeFull_ForADeclaredAccountWithNoTradesToday()
    {
        // The OTHER absence: a declared account that simply has not traded today. This must be indistinguishable
        // from "declared, full budget" — never conflated with the undeclared-account 404 above. The empty-window
        // honesty rule (gh#587's own acceptance criterion): a quiet day reads as full room, not as an error.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 1_000m, dailyDrawdownGovernor: 600m);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(0m);
        headroom.UnderGovernor.Should().Be(600m, "no activity today must read as the full governor, not zero or an error");
        headroom.UnderDailyLossLimit.Should().Be(1_000m);
        headroom.GovernorSpent.Should().BeFalse();
    }

    // -------------------------------------------------------------------------------------------------------
    // Reduced / spent.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldReduceUnderGovernor_OnALosingDay()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);
        await SeedClosedTradesTodayAsync(accountId, -400m);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.UnderGovernor.Should().Be(600m, "a losing day must reduce the governor's remaining room by exactly the day's loss");
        headroom.GovernorSpent.Should().BeFalse();
    }

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldReportGovernorSpent_WhenTheLossReachesTheGovernor()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);
        await SeedClosedTradesTodayAsync(accountId, -1_000m); // exactly spent

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.GovernorSpent.Should().BeTrue("a loss that exactly exhausts the governor must report spent, not merely near-zero room");
        headroom.UnderGovernor.Should().Be(0m);
    }

    // -------------------------------------------------------------------------------------------------------
    // Target progress — only WHEN stand-down is enabled and the target is met.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldReportDailyTargetReached_WhenStandDownIsEnabledAndTheTargetIsMet()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(
            client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 2_000m, dailyProfitTarget: 500m, stopForDayAtProfitTarget: true);
        await SeedClosedTradesTodayAsync(accountId, 500m);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayRealizedProfit.Should().Be(500m);
        headroom.DailyTargetReached.Should().BeTrue("the target is met and the account stands down at it — this must be visible");
    }

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldNotReportDailyTargetReached_WhenStandDownIsDisabled_EvenIfTheTargetIsMet()
    {
        // The guard this test exists to prove able to fail: an implementation that reports DailyTargetReached
        // from the profit figure ALONE (ignoring StopForDayAtProfitTarget) would pass every other target test in
        // this suite and only fail here. The profit is identical to the test above; only the posture differs.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(
            client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 2_000m, dailyProfitTarget: 500m, stopForDayAtProfitTarget: false);
        await SeedClosedTradesTodayAsync(accountId, 500m);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayRealizedProfit.Should().Be(500m, "the profit figure itself is unaffected by the stand-down posture");
        headroom.DailyTargetReached.Should().BeFalse(
            "reaching the target only means something when the account is configured to stand down at it");
    }

    // -------------------------------------------------------------------------------------------------------
    // Owner-scoping (R-20) — another operator's / another account's trades never enter the caller's headroom.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldNeverIncludeAnotherOperatorsTrades()
    {
        HttpClient operatorA = await AuthenticatedClientAsync();
        (HttpClient operatorB, _, _) = await CreateSecondOperatorAsync(operatorA);

        Guid accountA = await SetupAccountAsync(operatorA, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);
        Guid accountB = await SetupAccountAsync(operatorB, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);

        // A large loss on B's account -- if it ever leaked into A's projection, A's headroom would visibly shrink.
        await SeedClosedTradesTodayAsync(accountB, -900m);

        HeadroomResponse headroomA = await GetHeadroomAsync(operatorA, accountA);

        headroomA.DayLoss.Should().Be(0m, "operator B's loss must never enter operator A's headroom, even scoped to a DIFFERENT account");
        headroomA.UnderGovernor.Should().Be(1_000m);
    }

    [Fact(Skip = BlockedBy)]
    public async Task Headroom_ShouldReturn404_WhenAStrangerReadsAnotherOperatorsAccount()
    {
        HttpClient operatorA = await AuthenticatedClientAsync();
        (HttpClient operatorB, _, _) = await CreateSecondOperatorAsync(operatorA);

        Guid accountA = await SetupAccountAsync(operatorA, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);

        using HttpResponseMessage asB = await operatorB.GetAsync($"/accounts/{accountA}/risk/headroom");

        asB.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "a stranger reading another operator's account must get nothing that isn't theirs -- 404, never 403 or a real figure");
    }

    // -------------------------------------------------------------------------------------------------------
    // Realized-only — an open or unrealized position does not move the headroom.
    // -------------------------------------------------------------------------------------------------------

    [Fact(Skip = BlockedBy)]
    public async Task OpenPosition_ShouldNotMoveHeadroom()
    {
        // An OPEN position (no ClosedAt) is not a realized outcome, however large its unrealized figure. Counting
        // it would make headroom swing with the market rather than with what actually happened.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);
        await SeedOpenTradeAsync(accountId, unrealised: -5_000m); // would fully spend the governor if it counted

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(0m, "an open position must not move the headroom, however large its unrealized loss");
        headroom.GovernorSpent.Should().BeFalse();
    }

    [Fact(Skip = BlockedBy)]
    public async Task ClosedButUnrealizedPosition_ShouldNotMoveHeadroom()
    {
        // The distinct edge case gh#638 calls out separately from "open": ClosedAt set, RealizedPnL still null.
        // Only a trade with BOTH counts as a realized outcome (mirrors ConsistencyWindowReader's own filter).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, dailyLossLimit: 5_000m, dailyDrawdownGovernor: 1_000m);
        await SeedClosedButUnrealizedTradeAsync(accountId);

        HeadroomResponse headroom = await GetHeadroomAsync(client, accountId);

        headroom.DayLoss.Should().Be(0m, "a trade with ClosedAt but no RealizedPnL is not a realized outcome and must not move the headroom");
    }

    // -------------------------------------------------------------------------------------------------------
    // Helpers -- mirrors the harness pattern in ConsistencyTargetIntegrationTests / MultiTenantIsolationIntegrationTests.
    // -------------------------------------------------------------------------------------------------------

    private async Task<HeadroomResponse> GetHeadroomAsync(HttpClient client, Guid accountId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk/headroom");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        HeadroomResponse? headroom = await response.Content.ReadFromJsonAsync<HeadroomResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(headroom);
        return headroom;
    }

    /// <summary>Central midnight for "today," computed from the real Central "now" the suite runs under -- so the
    /// day-boundary test holds regardless of run date or daylight saving.</summary>
    private static DateTimeOffset CentralMidnightToday()
    {
        DateTimeOffset centralNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MarketClock.CentralTime);
        DateTime centralMidnight = centralNow.Date;
        TimeSpan offset = MarketClock.CentralTime.GetUtcOffset(centralMidnight);
        return new DateTimeOffset(centralMidnight, offset);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId, string Email)> CreateSecondOperatorAsync(HttpClient operatorClient)
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

        Guid userId = await QueryDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return (client, userId, email);
    }

    private async Task<Guid> SetupAccountAsync(
        HttpClient client,
        decimal? dailyLossLimit,
        decimal dailyDrawdownGovernor,
        decimal? dailyProfitTarget = null,
        bool stopForDayAtProfitTarget = false)
    {
        Guid accountId = await SetupUndeclaredAccountAsync(client);

        using HttpResponseMessage risk = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: dailyLossLimit,
                AccountProfitTarget: 10_000m,
                StartingBalance: 50_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: null,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 1_000m,
                DailyDrawdownGovernor: dailyDrawdownGovernor,
                DailyProfitTarget: dailyProfitTarget,
                StopForDayAtProfitTarget: stopForDayAtProfitTarget,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: null));
        risk.EnsureSuccessStatusCode();
        return accountId;
    }

    private async Task<Guid> SetupUndeclaredAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Headroom-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
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
        return accounts.First(account => account.CanTrade).Id;
    }

    private Task SeedClosedTradesTodayAsync(Guid accountId, params decimal[] realizedAmounts)
    {
        DateTimeOffset closedAt = CentralMidnightToday().AddHours(10); // safely inside today's Central day
        return Task.WhenAll(realizedAmounts.Select(realized => SeedClosedTradeAsync(accountId, closedAt, realized)));
    }

    private async Task SeedClosedTradeAsync(Guid accountId, DateTimeOffset closedAt, decimal realized)
    {
        Guid userId = await OperatorIdForAccountAsync(accountId);
        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(NewTrade(accountId, userId, closedAt, realized));
            await db.SaveChangesAsync();
        });
    }

    private async Task SeedOpenTradeAsync(Guid accountId, decimal unrealised)
    {
        Guid userId = await OperatorIdForAccountAsync(accountId);
        await ExecuteDbAsync(async db =>
        {
            Trade open = NewTrade(accountId, userId, closedAt: null, realized: unrealised);
            open.ExitPrice = null;
            db.Trades.Add(open);
            await db.SaveChangesAsync();
        });
    }

    private async Task SeedClosedButUnrealizedTradeAsync(Guid accountId)
    {
        Guid userId = await OperatorIdForAccountAsync(accountId);
        await ExecuteDbAsync(async db =>
        {
            Trade closed = NewTrade(accountId, userId, CentralMidnightToday().AddHours(10), realized: null);
            closed.RealizedPnL = null;
            db.Trades.Add(closed);
            await db.SaveChangesAsync();
        });
    }

    private static Trade NewTrade(Guid accountId, Guid userId, DateTimeOffset? closedAt, decimal? realized) => new()
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
        Mode = TradingMode.Practice,
        ClosedAt = closedAt,
    };

    /// <summary>The trade rows above are seeded straight at the DbContext (no venue involved -- this read touches
    /// none), so they need the OWNING operator's id, not necessarily the bootstrap operator's -- accounts created
    /// by operator B in the owner-scoping tests belong to B, not to <see cref="PostgresApiFactory.OperatorEmail"/>.</summary>
    private Task<Guid> OperatorIdForAccountAsync(Guid accountId) => QueryDbAsync(db => db.Accounts
        .IgnoreQueryFilters()
        .Where(account => account.Id == accountId)
        .Select(account => account.UserId)
        .SingleAsync());

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
