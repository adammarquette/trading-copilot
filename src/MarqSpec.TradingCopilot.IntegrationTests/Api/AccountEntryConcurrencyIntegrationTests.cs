using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the <b>account-entry serialization</b> gh#589 built (gh#724, of gh#619; R-5 / R-11 /
/// R-16, ADR-0007) — the concurrent proofs the unit tier structurally cannot run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> <c>ComposeAsync</c> sizes the R-5 gate off a <b>flat-account snapshot</b> and reserves
/// nothing for an entry already in flight, so two entry-transmits overlapping on one account is up to
/// <b>twice the approved risk</b> (gh#531). <see cref="IAccountEntryGuard"/>'s Postgres advisory lock is the only
/// thing preventing it, and only a real database can arbitrate two connections — an in-memory provider cannot
/// execute an advisory lock at all, which is why this tier exists.
/// </para>
/// <para>
/// <b>Why the assertions are shaped this way.</b> "Exactly one places" is trivially satisfiable by a test where
/// the second entry never actually raced — it would pass while proving nothing, which the QA contract's guard
/// discipline names as the failure mode of a concurrency test. So every case here forces the interleave: the peer
/// is launched from <b>inside</b> the first transmit, while the guard's lock is held and the venue call has not
/// returned. The decisive assertion is then that the venue's <b>ordinal call spans are disjoint</b> — overlap is
/// the direct witness of two entries composing the same flat snapshot, and unlike a wall-clock sample it cannot
/// pass by luck.
/// </para>
/// <para>
/// The peer is <b>sampled, never awaited inline</b>: awaiting it inside the transmit would deadlock on the very
/// lock under test, and an assertion thrown there would be swallowed by the caller's own fault handling and fail
/// silently. Reuses <see cref="StubbedVenuePostgresFactory"/>'s adversarial venue unmodified.
/// </para>
/// </remarks>
public class AccountEntryConcurrencyIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed clock for the deterministic direct-drive fire — firing reads no wall clock.</summary>
    private static DateTimeOffset Now { get; } = new(2026, 8, 6, 15, 0, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public AccountEntryConcurrencyIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TwoTakesOnOneAccount_ShouldTransmitExactlyOne_AndNeverOverlap()
    {
        // gh#724 case 1. Two DIFFERENT staged rows, same account: the rows do not contend (each has its own claim,
        // gh#530) so nothing but the account-entry guard stands between them and two simultaneous transmits.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-TwoTakes");
        await DeclareRiskProfileAsync(client, accountId);
        Guid first = await ArmAsync(client, accountId, ValidProposal("MES"));
        Guid second = await ArmAsync(client, accountId, ValidProposal("MNQ"));

        VenueFactory.ClearPlaceOrderFaults();
        VenueFactory.ResetPlaceOrderCallSpans();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;

        (HttpResponseMessage peer, bool peerFinishedInsideTheLock) =
            await RaceFromInsideTheFirstTransmitAsync(() => TakeAsync(client, first), () => TakeAsync(client, second));

        using (peer)
        {
            peerFinishedInsideTheLock.Should().BeFalse(
                "the second take must WAIT on the account-entry guard while the first transmit is in flight; if it "
                + "ran through, two entries composed the same flat-account snapshot and both could transmit");

            AssertTransmitsNeverOverlapped();

            VenueFactory.PlacedVenueOrderIds.Count.Should().BeLessThanOrEqualTo(
                restingBefore + 1,
                "at most ONE of two racing entries may rest at the venue — the second is refused, not queued into a "
                + "second transmit at twice the approved risk (gh#531)");
        }
    }

    [Fact]
    public async Task ASendRacingATake_ShouldTransmitExactlyOne_AndNeverOverlap()
    {
        // gh#724 case 2. The two OPERATOR entry paths against each other: a staged take and a direct send. They
        // reach the venue through different endpoints, so a guard applied to only one of them would still pass a
        // take-vs-take test and fail here.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-SendVsTake");
        await DeclareRiskProfileAsync(client, accountId);
        Guid staged = await ArmAsync(client, accountId, ValidProposal("MES"));

        VenueFactory.ClearPlaceOrderFaults();
        VenueFactory.ResetPlaceOrderCallSpans();
        int restingBefore = VenueFactory.PlacedVenueOrderIds.Count;

        (HttpResponseMessage peer, bool peerFinishedInsideTheLock) = await RaceFromInsideTheFirstTransmitAsync(
            () => TakeAsync(client, staged),
            () => client.PostAsJsonAsync($"/accounts/{accountId}/orders", ValidProposal("MNQ")));

        using (peer)
        {
            peerFinishedInsideTheLock.Should().BeFalse(
                "a direct send must serialize behind an in-flight take on the same account, exactly as another take "
                + "would — the guard is per ACCOUNT, not per endpoint");

            AssertTransmitsNeverOverlapped();

            VenueFactory.PlacedVenueOrderIds.Count.Should().BeLessThanOrEqualTo(
                restingBefore + 1, "at most ONE of the two racing entries may rest at the venue");
        }
    }

    [Fact]
    public async Task TheFiringPass_ShouldSkipAndReturn_RatherThanStallBehindAnInFlightTake()
    {
        // gh#724 case 4. The fire path takes the NON-BLOCKING pg_try_advisory_lock, deliberately: the firing
        // watcher serves every pending conditional across every account, so parking it behind one account's
        // in-flight take would stall protection for all of them. The blocking form would still make the two
        // previous cases pass — this is the only case that tells the two lock modes apart.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-FireLock");
        await DeclareRiskProfileAsync(client, accountId);
        Guid staged = await ArmAsync(client, accountId, ValidProposal("MES"));
        (Guid conditionalId, string contractKey) = await ArmConditionalAsync(client, accountId, "MYM", trigger: 5_010m);

        VenueFactory.ClearPlaceOrderFaults();
        VenueFactory.ResetPlaceOrderCallSpans();

        // Drive the firing pass from INSIDE the take's transmit: the take holds the account lock and the venue
        // call has not returned, which is precisely the contention the try-lock exists for.
        // SAMPLED WHILE THE LOCK IS HELD, never after. Checking "did it return?" once the take has completed is
        // true either way -- a blocking lock simply parks until the take releases and then returns, so the flag
        // reads true and the stall goes unseen. (Measured: with pg_advisory_lock substituted for the try-lock this
        // suite still passed, and only the run DURATION moved, 6s -> 38s. That is the QA contract's "passes with
        // and against the defect" exactly, so the observation had to move inside the lock.)
        Task<int>? firePass = null;
        bool firePassReturnedInsideTheLock = false;
        bool launched = false;
        VenueFactory.OnPlaceOrder(async () =>
        {
            if (launched)
            {
                return;
            }

            launched = true;
            firePass = FireQuoteAsync(contractKey, bid: 5_009m, ask: 5_010m, Now);

            // Sampled, never awaited here: awaiting a BLOCKING acquisition inside the holder's own transmit would
            // deadlock the test rather than fail it.
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            firePassReturnedInsideTheLock = firePass.IsCompleted;
        });

        using HttpResponseMessage take = await TakeAsync(client, staged);
        VenueFactory.OnPlaceOrder(null);

        launched.Should().BeTrue("the interleave must have run — otherwise nothing was raced");
        firePass.Should().NotBeNull();
        int acted = await firePass!;

        firePassReturnedInsideTheLock.Should().BeTrue(
            "the firing pass must RETURN WHILE the take still holds the account lock — pg_try_advisory_lock fails "
            + "fast and the pass moves on. A blocking acquisition would park the watcher here, and with it every "
            + "OTHER account's conditionals, until this one take completed");
        acted.Should().Be(
            0, "the contended account is skipped this pass, to be re-decided on the next quote — never fired anyway");

        AssertTransmitsNeverOverlapped();

        await ExecuteDbContextAsync(async database =>
        {
            ConditionalOrderRecord record = await database.ConditionalOrders
                .IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == conditionalId);
            record.Status.Should().Be(
                ConditionalStatus.Pending,
                "a skipped conditional stays re-decidable — a contended pass must not resolve or strand it");
        });
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

    /// <summary>
    /// Runs <paramref name="first"/> and launches <paramref name="peer"/> from inside its venue transmit — the worst
    /// possible moment: the account lock is held and the venue call has not returned. The peer is sampled rather
    /// than awaited inline (awaiting would deadlock on the lock under test), then awaited once the first completes.
    /// </summary>
    private async Task<(HttpResponseMessage Peer, bool PeerFinishedInsideTheLock)> RaceFromInsideTheFirstTransmitAsync(
        Func<Task<HttpResponseMessage>> first,
        Func<Task<HttpResponseMessage>> peer)
    {
        Task<HttpResponseMessage>? peerCall = null;
        bool finishedInsideTheLock = false;
        bool launched = false;

        VenueFactory.OnPlaceOrder(async () =>
        {
            if (launched)
            {
                return;
            }

            launched = true;
            peerCall = peer();
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            finishedInsideTheLock = peerCall.IsCompleted;
        });

        using HttpResponseMessage _ = await first();
        VenueFactory.OnPlaceOrder(null);

        peerCall.Should().NotBeNull("the interleave must have run — otherwise nothing was raced");
        return (await peerCall!, finishedInsideTheLock);
    }

    /// <summary>
    /// The decisive proof of serialization: two transmits whose ordinal spans interleave were in flight on the same
    /// account at once, each having composed the same flat snapshot. Unlike a timing sample this cannot pass by luck.
    /// </summary>
    private void AssertTransmitsNeverOverlapped()
    {
        IReadOnlyList<(long Entered, long Left)> spans = VenueFactory.PlaceOrderCallSpans;
        for (int outer = 0; outer < spans.Count; outer++)
        {
            for (int inner = outer + 1; inner < spans.Count; inner++)
            {
                (spans[inner].Entered > spans[outer].Left || spans[outer].Entered > spans[inner].Left)
                    .Should().BeTrue(
                        "entry-transmits on one account must never overlap — spans {0} and {1} did",
                        spans[outer], spans[inner]);
            }
        }
    }

    private Task<HttpResponseMessage> TakeAsync(HttpClient client, Guid orderId) =>
        client.PostAsync($"/orders/{orderId}/take", null);

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<int> FireQuoteAsync(string contractKey, decimal bid, decimal ask, DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ConditionalFiringService firing = scope.ServiceProvider.GetRequiredService<ConditionalFiringService>();
        return await firing.ProcessQuoteAsync(contractKey, bid, ask, now, CancellationToken.None);
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

    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId, decimal maxDrawdownPerTrade = 300m)
    {
        DeclareRiskProfileRequest declareReq = new(
            DailyLossLimit: 1_000m,
            AccountProfitTarget: 3_000m,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 2_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: maxDrawdownPerTrade,
            DailyDrawdownGovernor: 600m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must arm cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private async Task<(Guid Id, string ContractKey)> ArmConditionalAsync(
        HttpClient client, Guid accountId, string symbol, decimal trigger)
    {
        CreateConditionalOrderRequest request = new(
            Order: ValidProposal(symbol),
            TriggerPrice: trigger,
            TriggerDirection: ConditionalCrossDirection.RisesTo);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's conditional must arm cleanly");
        ConditionalOrderResponse? armed = await response.Content.ReadFromJsonAsync<ConditionalOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(armed);

        string contractKey = await ExecuteDbContextQueryAsync(database => database.ConditionalOrders
            .IgnoreQueryFilters().AsNoTracking()
            .Where(candidate => candidate.Id == armed.ConditionalOrderId)
            .Select(candidate => candidate.Instrument)
            .SingleAsync());

        return (armed.ConditionalOrderId, contractKey);
    }

    private static SendOrderRequest ValidProposal(string symbol, int quantity = 1) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: quantity,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    private async Task<T> ExecuteDbContextQueryAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }
}
