using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// The <b>live-host</b> half of the cross-asset context-price coverage (gh#705, of gh#608; gh#496, gh#495, gh#411 ·
/// R-1, R-17 · ADR-0001). The producer-contract half — own event type, deterministic id — ships in
/// <c>ContextIngestionIntegrationTests</c> (gh#608, PR #706) against the plain <c>PostgresApiFactory</c>; these need
/// <c>StopPromotionHost</c> and <c>ConditionalOrderHost</c> actually <i>running</i>, so they sit on the gh#557
/// live-host fixture shape (<see cref="ContextLiveHostPostgresFactory"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Case 2 is the one that proves the cross-asset design.</b> That a context print lands under its own type is a
/// storage fact; that the two safety-critical watchers <i>cannot act on it</i> is the safety property. The failure
/// it guards is concrete and reachable: any change that let an execution watcher read a context print — decoding
/// <c>market.context-trade</c>, or a "unified market data" refactor that synthesised <c>bid == ask == price</c> —
/// would promote a stop and fire an entry on a futures contract off an equity print with no book behind it.
/// </para>
/// <para>
/// <b>Why this suite cannot pass vacuously.</b> Two constructions do that work, and both are deliberate.
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>The watchers are proven to have SEEN the print.</b> The suite waits until both consumer cursors have advanced
/// <i>past</i> the context print's own sequence before asserting that nothing happened. Each host commits its
/// cursor only after processing every event in the batch, so a cursor past the print means the print was consumed
/// and declined — not merely that the poll had not reached it yet. Without this the whole case would read "nothing
/// happened because nothing had run".
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The prove-red positive control is inside the same test</b> (gh#705 specifies it): the same price is re-appended
/// as a <c>market.quote</c> on the matching tradeable contract, and the stop <b>must</b> promote and the conditional
/// <b>must</b> fire. It runs on the very fixtures the negative half asserted were untouched, so "nothing happened
/// because nothing was configured" cannot survive it.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>The venue seam is the sole double</b> (QA contract §1) and stays adversarial: it feeds the open position and
/// records what was transmitted, and never decides whether to promote or fire. The context source is likewise a
/// pure feed — it streams the prints it is handed and reports nothing about the event type, the id, or the append,
/// all of which are read back from Postgres.
/// </para>
/// </remarks>
public class ContextIngestionLiveHostIntegrationTests : IClassFixture<ContextLiveHostPostgresFactory>
{
    /// <summary>The tradeable symbol the watchers are armed on; the venue stub resolves it to <c>MESM25</c>.</summary>
    private const string TradeableSymbol = "MES";

    /// <summary>
    /// The price the context print carries. It crosses <b>both</b> watchers' conditions: the hidden stop's 4-tick
    /// band above its 5295 stop (promote at ≤ 5296) and the conditional's <c>FallsTo</c> 5297 trigger.
    /// </summary>
    private const decimal CrossingPrice = 5_296m;

    /// <summary>The contract key the case-3 quote rides — its own, so no other case's fixtures can answer for it.</summary>
    private const string UnrelatedContractKey = "CTX705-NOSOURCE";

    /// <summary>
    /// The venue account key the hidden stop's account holds. Deliberately not one the venue stub's discovery hands
    /// out, so the open position seeded for it belongs to this account and no other.
    /// </summary>
    private const string StopAccountVenueKey = "CTX705-9001";

    private readonly ContextLiveHostPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ContextIngestionLiveHostIntegrationTests(ContextLiveHostPostgresFactory factory)
    {
        _factory = factory;
    }

    private sealed record LoginTokenResponse(string Token);

    // =============================================================================================================
    // gh#608 case 2 — the execution watchers are blind to a context print.
    // =============================================================================================================

    [Fact]
    public async Task ExecutionWatchers_ShouldIgnoreAContextPrint_ButActOnTheSamePriceArrivingAsAQuote()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid conditionalAccountId = await SetupTradeableAccountAsync(client, "Topstep-CtxBlindConditional");
        await DeclareRiskProfileAsync(client, conditionalAccountId);

        // A pending conditional whose FallsTo trigger the crossing price crosses (a long transacts on the ask).
        (Guid conditionalId, string contractKey) =
            await ArmConditionalAsync(client, conditionalAccountId, trigger: 5_297m);

        // …and a hidden stop on the SAME contract whose 4-tick band the same price brings it inside (a long's
        // promotion is measured on the bid). One price, both watchers — so a single event decides the whole case.
        //
        // The stop lives on its OWN account, and that is load-bearing rather than tidiness. gh#589's no-stacking
        // rule refuses to fire a conditional while its account holds any outstanding entry, so a working order
        // seeded for the promotion case on the SAME account would leave the conditional Pending for a reason that
        // has nothing to do with event types — the negative half would pass for the wrong reason and the positive
        // control could never pass at all. (CI caught exactly that; the rule is correct and the fixture was not.)
        // Its venue account key is its own too, so the open position the promotion needs is invisible to the
        // conditional's account.
        Guid orderId = await SeedIsolatedHiddenStopAsync(operatorId, contractKey);
        VenueFactory.SeedPosition(StopAccountVenueKey, contractKey, netQuantity: 2);

        (await StagingAsync(orderId)).Should().Be(
            StopStaging.Hidden, "the fixture must start from a stop that is genuinely promotable");
        int placementsBefore = VenueFactory.TotalPlacedOrderCount;

        // ---- The negative half: context prints, at a price that would cross both conditions ------------------

        // Two prints. The first is the ordinary shape — the context source's own equity contract. The second is the
        // adversarial one: a context print whose contract key IS the futures contract the watchers are armed on, so
        // the ONLY thing standing between it and the execution path is the event type.
        await IngestContextPrintsAsync(
            VenueContractId.Create(VenueId.Parse("finnhub"), ContextLiveHostPostgresFactory.ContextSymbol),
            VenueContractId.Create(VenueId.Parse("finnhub"), contractKey));

        long contextSequence = await LatestContextPrintSequenceAsync();

        // The heart of the case: both watchers must be proven to have CONSUMED the print before "nothing happened"
        // means anything. Each commits its cursor only after processing the whole batch, so a cursor past this
        // sequence is the log's own witness that the print was read and declined.
        await WaitUntilAsync(
            async () => await CursorAsync(StopPromotionHost.ConsumerGroup) >= contextSequence
                && await CursorAsync(ConditionalOrderHost.ConsumerGroup) >= contextSequence,
            $"both execution watchers to consume past the context print at sequence {contextSequence}");

        (await StagingAsync(orderId)).Should().Be(
            StopStaging.Hidden,
            "a context print carries no book, so the stop-promotion watcher must not promote off it — promoting "
            + "would rest a native protective stop at the exchange off an equity print nobody could transact at");
        (await ConditionalStatusAsync(conditionalId)).Should().Be(
            ConditionalStatus.Pending,
            "a context print must never fire a synthetic entry — a fired conditional is a real order at the broker");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            placementsBefore, "nothing at all may reach the venue on the strength of a context print");

        // ---- The prove-red positive control (gh#705 specifies it) --------------------------------------------

        // The same price, now with a book behind it, on the matching tradeable contract — through the SHIPPED quote
        // producer, so the two halves differ only in which producer (and so which event type) carried the price. If
        // this did not act, the negative half above would be vacuous — "nothing happened because nothing was
        // configured", exactly the failure the QA contract's guard discipline names.
        int quotesAppended = await IngestQuotesAsync(contractKey, bid: 5_295.75m, ask: CrossingPrice);
        quotesAppended.Should().Be(1, "the positive control's quote must actually have reached the log");

        long quoteSequence = await LatestQuoteSequenceAsync(contractKey);

        await WaitUntilAsync(
            async () => await StagingAsync(orderId) == StopStaging.Native,
            $"the stop-promotion watcher to promote the hidden stop off the market.quote at sequence {quoteSequence}");
        await WaitUntilAsync(
            async () => await ConditionalStatusAsync(conditionalId) == ConditionalStatus.Fired,
            $"the conditional-order watcher to fire the pending conditional off the market.quote at {quoteSequence}");

        VenueFactory.TotalPlacedOrderCount.Should().BeGreaterThan(
            placementsBefore,
            "the promoted native stop and the fired entry both reach the venue — which is exactly what the identical "
            + "price did NOT do while it was a context print");
    }

    // =============================================================================================================
    // gh#608 case 3 — a context outage does not disturb tradeable ingestion (the "source cannot be constructed" half).
    // =============================================================================================================

    [Fact]
    public async Task TradeableIngestion_ShouldKeepRunning_WhenContextSymbolsAreConfiguredWithoutASource()
    {
        // gh#496's most important acceptance criterion, in the shape a real deployment meets it: an operator
        // configured ContextIngestion__Symbols but no Finnhub__ApiKey, so NO context source is registered and none
        // can be constructed. .NET's default BackgroundServiceExceptionBehavior is StopHost — an exception escaping
        // ContextIngestionHost.ExecuteAsync would take the WHOLE application down, the quote feed, the auto-flatten
        // watchdog and the kill switch with it, because an optional colour feed was misconfigured.

        // Building the client is what boots the host and its background services — nothing below can be observed
        // until they have started.
        using HttpClient client = _factory.CreateClient();

        // First, prove the context path really is broken. Without this the rest asserts nothing: a platform that
        // ingests tradeable data while context is perfectly healthy is not evidence of independence.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Any(entry =>
                entry.Message.Contains("no context source is registered", StringComparison.OrdinalIgnoreCase))),
            "the context host to report that the configured symbols have no source to stream from");

        // The application is still up — the StopHost failure mode would have taken the web host with it.
        using HttpResponseMessage health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(
            HttpStatusCode.OK, "a context source that cannot be constructed must never stop the platform");

        // Tradeable quotes still land: the SHIPPED producer, over the venue seam, into the real event log.
        int appended = await IngestQuotesAsync(UnrelatedContractKey, bid: 4_999.75m, ask: 5_000m);
        appended.Should().Be(1, "the tradeable producer is unaffected by the context feed's absence");

        long sequence = await LatestQuoteSequenceAsync(UnrelatedContractKey);

        // …and the safety-critical consumers are alive to read them. A cursor that advances is the only witness
        // that distinguishes "running" from "the host object still exists".
        await WaitUntilAsync(
            async () => await CursorAsync(StopPromotionHost.ConsumerGroup) >= sequence,
            $"the stop-promotion consumer's cursor to advance past sequence {sequence}");
        await WaitUntilAsync(
            async () => await CursorAsync(ConditionalOrderHost.ConsumerGroup) >= sequence,
            $"the conditional-order consumer's cursor to advance past sequence {sequence}");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Runs the shipped context producer over a feed-only source, as the context host does per subscription.</summary>
    private async Task IngestContextPrintsAsync(params VenueContractId[] contracts)
    {
        foreach (VenueContractId contract in contracts)
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            ContextTradeIngestionService ingestion =
                scope.ServiceProvider.GetRequiredService<ContextTradeIngestionService>();

            int appended = await ingestion.IngestContextTradesAsync(
                new StubContextSource(new ContextTrade(DateTimeOffset.UtcNow, new Price(CrossingPrice), Size: 100m)),
                contract,
                CancellationToken.None);

            appended.Should().Be(1, "the context print under test must actually have reached the log");
        }
    }

    /// <summary>Runs the shipped quote producer over a feed-only market-data source — the tradeable path's producer.</summary>
    private async Task<int> IngestQuotesAsync(string contractKey, decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        QuoteIngestionService ingestion = scope.ServiceProvider.GetRequiredService<QuoteIngestionService>();

        return await ingestion.IngestQuotesAsync(
            new StubQuoteSource(new Quote(DateTimeOffset.UtcNow, new Price(bid), new Price(ask), null, null)),
            VenueContractId.Create(VenueId.Parse("projectx"), contractKey),
            CancellationToken.None);
    }

    private Task<long> LatestContextPrintSequenceAsync() => QueryDbAsync(db => db.Events
        .Where(row => row.Type == ContextTradeIngestionService.ContextTradeEventType)
        .MaxAsync(row => row.Sequence));

    private Task<long> LatestQuoteSequenceAsync(string contractKey) => QueryDbAsync(async db =>
    {
        // Payload is `jsonb`, so a `.Contains` predicate would translate to a string LIKE Postgres refuses on that
        // column type — the type filter runs in SQL and the contract filter in memory (the shipped half's note).
        List<QuoteRow> quotes = await db.Events
            .Where(row => row.Type == QuoteIngestionService.QuoteEventType)
            .OrderBy(row => row.Sequence)
            .Select(row => new QuoteRow(row.Sequence, row.Payload))
            .ToListAsync();

        List<QuoteRow> mine = [.. quotes.Where(row => row.Payload.Contains(contractKey, StringComparison.Ordinal))];
        mine.Should().NotBeEmpty($"the quote on {contractKey} must have reached the real event log");

        return mine.Max(row => row.Sequence);
    });

    private sealed record QuoteRow(long Sequence, string Payload);

    private Task<long?> CursorAsync(string consumerGroup) => WithScopeAsync(services =>
        services.GetRequiredService<IEventLog>().GetCursorAsync(consumerGroup, CancellationToken.None));

    private Task<StopStaging> StagingAsync(Guid orderId) => QueryDbAsync(db => db.StopPlans
        .IgnoreQueryFilters().AsNoTracking()
        .Where(plan => plan.OrderId == orderId)
        .Select(plan => plan.Staging)
        .SingleAsync());

    private Task<ConditionalStatus> ConditionalStatusAsync(Guid conditionalId) => QueryDbAsync(db => db.ConditionalOrders
        .IgnoreQueryFilters().AsNoTracking()
        .Where(conditional => conditional.Id == conditionalId)
        .Select(conditional => conditional.Status)
        .SingleAsync());

    private Task<Guid> OperatorIdAsync() => QueryDbAsync(db => db.Users
        .AsNoTracking()
        .Where(user => user.Email == PostgresApiFactory.OperatorEmail)
        .Select(user => user.Id)
        .FirstAsync());

    /// <summary>
    /// Seeds a working long (entry 5300 / stop 5295 / safety 5290, size 2) and its <b>hidden</b> stop plan with a
    /// 4-tick band — promotable on a bid at or below 5296, which is <see cref="CrossingPrice"/> — on an account of
    /// its <b>own</b>, with its own venue account key.
    /// </summary>
    /// <remarks>
    /// Written straight to the database rather than discovered through the API deliberately: discovery hands every
    /// connection the same venue account keys, and this account needs one nothing else shares so the open position
    /// it requires cannot be seen by the conditional's account (see the case's note on gh#589's no-stacking rule).
    /// </remarks>
    private async Task<Guid> SeedIsolatedHiddenStopAsync(Guid operatorId, string contractKey)
    {
        Guid orderId = Guid.NewGuid();
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Firms.Add(new Firm
            {
                Id = firmId,
                UserId = operatorId,
                Name = "Topstep-CtxBlindStop",
                Type = FirmType.PropFirm,
            });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = operatorId,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = "topstep-main",
            });
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = operatorId,
                ConnectionId = connectionId,
                VenueAccountKey = StopAccountVenueKey,
                Name = "PRAC-50K-CTX705",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
            });
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = contractKey,
                Symbol = TradeableSymbol,
                Side = OrderSide.Buy,
                Size = 2,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                EntryPrice = 5_300m,
                WorkingStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ReferencePrice = 5_300m,
                TickSize = 0.25m,
                PointValue = 5m,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_300m,
                ActualStopPrice = 5_295m,
                SafetyStopPrice = 5_290m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = StopStaging.Hidden,
            });
            await db.SaveChangesAsync();
        });

        return orderId;
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

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered tradeable account.</summary>
    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
            DailyLossLimit: 1_000m,
            AccountProfitTarget: 3_000m,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 2_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: 300m,
            DailyDrawdownGovernor: 600m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Arms a pending <c>FallsTo</c> conditional through the real endpoint; returns its id and contract key.</summary>
    private async Task<(Guid Id, string ContractKey)> ArmConditionalAsync(
        HttpClient client, Guid accountId, decimal trigger)
    {
        CreateConditionalOrderRequest request = new(
            Order: new SendOrderRequest(
                Symbol: TradeableSymbol,
                TickSize: 0.25m,
                PointValue: 5m,
                Side: OrderSide.Buy,
                Quantity: 1,
                Entry: 5_296m,
                Stop: 5_286m,
                SafetyStop: 5_276m,
                ReferencePrice: 5_296m,
                Type: OrderType.Market),
            TriggerPrice: trigger,
            TriggerDirection: ConditionalCrossDirection.FallsTo);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/conditional", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's conditional must arm cleanly");

        ConditionalOrderResponse? armed = await response.Content.ReadFromJsonAsync<ConditionalOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(armed);
        armed.Status.Should().Be(nameof(ConditionalStatus.Pending), "an armed conditional rests Pending");

        string contractKey = await QueryDbAsync(db => db.ConditionalOrders
            .IgnoreQueryFilters().AsNoTracking()
            .Where(conditional => conditional.Id == armed.ConditionalOrderId)
            .Select(conditional => conditional.Instrument)
            .SingleAsync());

        return (armed.ConditionalOrderId, contractKey);
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or fails with what the hosts said in the meantime. The log
    /// tail is not decoration: a background watcher that declines to act says so in its own log and nowhere else, so
    /// a bare timeout leaves the next reader guessing at which of several refusal paths was taken.
    /// </summary>
    private async Task WaitUntilAsync(
        Func<Task<bool>> condition, string because, int attempts = 200, int delayMs = 250)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        string[] watcherLogs = [.. _factory.Logs.Entries
            .Where(entry => entry.Message.Contains("Conditional order", StringComparison.Ordinal)
                || entry.Message.Contains("stop", StringComparison.OrdinalIgnoreCase))
            .TakeLast(10)
            .Select(entry => $"  [{entry.Level}] {entry.Message}")];

        string tail = watcherLogs.Length == 0
            ? "  (the watchers logged nothing about a stop or a conditional)"
            : string.Join(Environment.NewLine, watcherLogs);

        throw new TimeoutException(
            $"Timed out waiting for {because}.{Environment.NewLine}What the watchers said:{Environment.NewLine}{tail}");
    }

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query) => WithScopeAsync(services =>
        query(services.GetRequiredService<TradingCopilotDbContext>()));

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    /// <summary>
    /// A context source that streams exactly the prints it is handed (the shipped half's shape). It <b>feeds inputs
    /// and computes nothing</b> — it never reports the event type, the id, or whether the append happened, so every
    /// assertion is read back from Postgres rather than from the double.
    /// </summary>
    private sealed class StubContextSource : IContextMarketDataSource
    {
        private readonly ContextTrade[] _prints;

        public StubContextSource(params ContextTrade[] prints)
        {
            _prints = prints;
        }

        public VenueId Id { get; } = VenueId.Parse("finnhub");

        public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.ContextTrades);

        public int AdapterLogicVersion => 1;

        public Task<ResolvedContract> ResolveContractAsync(
            InstrumentId instrument, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These cases drive the stream directly with an already-resolved contract.");

        public async IAsyncEnumerable<ContextTrade> StreamContextTradesAsync(
            VenueContractId contract,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ContextTrade print in _prints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return print;
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// The tradeable counterpart: a market-data source that streams exactly the quotes it is handed. Same
    /// discipline — it feeds a bid and an ask and answers nothing about what the system did with them.
    /// </summary>
    private sealed class StubQuoteSource : IMarketDataSource
    {
        private readonly Quote[] _quotes;

        public StubQuoteSource(params Quote[] quotes)
        {
            _quotes = quotes;
        }

        public VenueId Id { get; } = VenueId.Parse("projectx");

        public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.Quotes);

        public int AdapterLogicVersion => 1;

        public Task<ResolvedContract> ResolveContractAsync(
            InstrumentId instrument, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These cases drive the stream directly with an already-resolved contract.");

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            VenueContractId contract,
            DateTimeOffset from,
            DateTimeOffset to,
            TimeSpan barSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The tradeable-continuity case streams live quotes; it asks for no history.");

        public async IAsyncEnumerable<Quote> StreamQuotesAsync(
            VenueContractId contract,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (Quote quote in _quotes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return quote;
                await Task.Yield();
            }
        }
    }
}
