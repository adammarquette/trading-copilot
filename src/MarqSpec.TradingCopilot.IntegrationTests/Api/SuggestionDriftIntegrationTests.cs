using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the suggestion-drift consumer's <b>guarded database update</b> and its
/// <b>symbol/contract bridge</b> (gh#632 ⇒ gh#546, R-4 / R-12, ADR-0013) — authored from the spec, not the
/// coding implementation (QA contract §1). Two things the unit tier cannot reach by construction: <see cref="ISuggestionDrift.MarkDriftedStaleAsync"/>
/// is an <c>ExecuteUpdate</c> the EF in-memory provider cannot run (the gh#530 lesson — the unit suite drives a
/// faithful in-memory double instead, so the <b>real</b> SQL predicate is unverified anywhere else), and the
/// symbol→contract bridge needs a real venue resolve. This suite drives <see cref="ISuggestionDrift"/> directly
/// for the guarded-update geometry (isolating the SQL predicate from the discovery/resolve plumbing around it) and
/// <see cref="SuggestionDriftService"/> directly for the bridge, against real PostgreSQL with the applied
/// migrations. Hosts are suppressed (<see cref="OcoExitTestPostgresFactory"/>, the sanctioned reuse other suites
/// already make of it purely for host suppression — e.g. <c>BarStoreIntegrationTests</c>,
/// <c>IndicatorProjectionIntegrationTests</c>) so nothing races the seeded surface; the consumer's <b>live host</b>
/// wiring, cursor and teardown are the separate <see cref="SuggestionDriftConsumerLifecycleIntegrationTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prove-red, by inspection.</b> This sandbox has neither a .NET SDK nor Docker, so the suite could not be
/// executed here — CI's pre-merge job is the first real run. Each guard below is designed to fail on a specific,
/// named mutation of <c>SuggestionDrift.MarkDriftedStaleAsync</c>'s predicate (the exact-boundary tests fail if
/// <c>&gt;</c> becomes <c>&gt;=</c>; the geometry tests fail if a side's bound is dropped or the band is scaled
/// wrong; the forward-only tests fail if the <c>WHERE State == Active</c> guard widens to
/// <c>IN (Active, Stale)</c> like the sibling expire sweep's), reasoned through by manual inspection against
/// <see cref="Domain.Suggestions.SuggestionLifecycle.HasDrifted"/> rather than an executed red run.
/// </para>
/// <para>
/// <b>The venue seam is the sole sanctioned double</b> (QA contract) — adversarial: <c>ResolveContractAsync</c>
/// feeds a deterministic symbol→contract mapping it never lets the caller override with the production-computed
/// answer, and the "venue cannot resolve" case (<see cref="AdversarialTestProjectXVenueFactory.MakeResolveContractThrow"/>,
/// added by this suite) makes it throw rather than hand back anything at all.
/// </para>
/// </remarks>
public class SuggestionDriftIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    // A round-number band matching ES's real defaults (8 ticks * 0.25 = 2.00) purely for readable prices; these
    // tests drive ISuggestionDrift directly, so the band is caller-supplied and not read from any configuration.
    private const decimal Entry = 5_000.00m;
    private const decimal Band = 2.00m;
    private static DateTimeOffset Now { get; } = new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SuggestionDriftIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // 1. The guarded update's geometry — the SQL predicate must equal HasDrifted, for both sides.
    // =============================================================================================================

    [Theory]
    [InlineData(OrderSide.Buy, "an ask that rose past the band")]
    [InlineData(OrderSide.Sell, "a bid that rose past the band")]
    public async Task Drift_ShouldMarkStale_WhenPriceRisesPastTheBand(OrderSide side, string because)
    {
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        decimal driftedPrice = Entry + Band + 0.25m; // one tick past the band
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, side, Entry, SuggestionState.Active, Now.AddHours(1), instrument: "ES");

        int marked = await MarkDriftedStaleAsync("ES", side, bid: driftedPrice, ask: driftedPrice, suggestionId);

        marked.Should().Be(1, because);
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Stale);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "an ask that fell past the band")]
    [InlineData(OrderSide.Sell, "a bid that fell past the band")]
    public async Task Drift_ShouldMarkStale_WhenPriceFallsPastTheBand(OrderSide side, string because)
    {
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        decimal driftedPrice = Entry - Band - 0.25m; // one tick past the band, the other direction
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, side, Entry, SuggestionState.Active, Now.AddHours(1), instrument: "ES");

        int marked = await MarkDriftedStaleAsync("ES", side, bid: driftedPrice, ask: driftedPrice, suggestionId);

        marked.Should().Be(1, because);
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task Drift_ShouldStayActive_WhenTheMoveIsWithinTolerance()
    {
        // The control: HasDrifted is a strict-inequality test, not "moved at all" — a move short of the band must
        // change nothing, or the geometry tests above would be meaningless (anything would go Stale).
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        decimal withinBand = Entry + Band - 0.25m; // one tick short of the band
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Active, Now.AddHours(1), instrument: "ES");

        int marked = await MarkDriftedStaleAsync("ES", OrderSide.Buy, bid: withinBand, ask: withinBand, suggestionId);

        marked.Should().Be(0, "a move short of the band is not drift");
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Active);
    }

    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public async Task Drift_ShouldStayActive_AtTheExactBoundary_PinningTheStrictGreaterThan(OrderSide side)
    {
        // |price - entry| == band, exactly — HasDrifted's boundary is exclusive (>), matching the take-time
        // re-check (gh#548): "exactly at tolerance is not yet drifted." A predicate that widened to >= would mark
        // this Stale and this test would go red.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        decimal atBoundary = Entry + Band;
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, side, Entry, SuggestionState.Active, Now.AddHours(1), instrument: "ES");

        int marked = await MarkDriftedStaleAsync("ES", side, bid: atBoundary, ask: atBoundary, suggestionId);

        marked.Should().Be(0, "exactly at the band is not yet drifted — the boundary is exclusive");
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Active);
    }

    [Fact]
    public async Task Drift_ShouldStampStateChangedAtInTheSameWriteAsTheStateMove()
    {
        // A single UPDATE statement's two SET clauses (ExecuteUpdateAsync), so a reader can never observe the
        // state moved without the audit stamp, or the stamp set without the move — true atomicity, not "usually
        // together." If the writer instead issued two separate statements, a fault between them could leave State
        // moved with StateChangedAt still null (or vice versa), which this single read-back would catch.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Active, Now.AddHours(1), instrument: "ES");
        decimal driftedPrice = Entry + Band + 1.00m;

        await MarkDriftedStaleAsync("ES", OrderSide.Buy, bid: driftedPrice, ask: driftedPrice, suggestionId, now: Now);

        Suggestion stored = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == suggestionId));
        stored.State.Should().Be(SuggestionState.Stale);
        stored.StateChangedAt.Should().Be(Now, "the audit stamp is the caller's clock, written atomically with the move");
    }

    // =============================================================================================================
    // 2. Forward-only — WHERE State == Active only, never Stale/ExpiredVoid; and no backward transition under a
    //    concurrent race with the sibling expire sweep's WHERE State IN (Active, Stale).
    // =============================================================================================================

    [Fact]
    public async Task Drift_ShouldNotTouch_AStaleSuggestion_EvenWhenThePriceIsWildlyDrifted()
    {
        // The sharp distinction from ISuggestionExpiry's WHERE State IN (Active, Stale): drift's guard is
        // Active-ONLY. A predicate that widened to include Stale (matching the sweep's shape) would flip this row
        // and its StateChangedAt, and this test would catch it.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        DateTimeOffset originalStamp = Now.AddMinutes(-10);
        Guid suggestionId = await SeedSuggestionAsync(
            accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Stale, Now.AddHours(1), instrument: "ES", stateChangedAt: originalStamp);

        int marked = await MarkDriftedStaleAsync("ES", OrderSide.Buy, bid: Entry + 100m, ask: Entry + 100m, suggestionId);

        marked.Should().Be(0, "an already-Stale row is never re-touched — the guard is Active-only");
        Suggestion stored = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == suggestionId));
        stored.State.Should().Be(SuggestionState.Stale);
        stored.StateChangedAt.Should().Be(originalStamp, "an untouched row's audit stamp is untouched too");
    }

    [Fact]
    public async Task Drift_ShouldNotTouch_AnExpiredVoidSuggestion()
    {
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        DateTimeOffset originalStamp = Now.AddMinutes(-10);
        Guid suggestionId = await SeedSuggestionAsync(
            accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.ExpiredVoid, Now.AddHours(1), instrument: "ES", stateChangedAt: originalStamp);

        int marked = await MarkDriftedStaleAsync("ES", OrderSide.Buy, bid: Entry + 100m, ask: Entry + 100m, suggestionId);

        marked.Should().Be(0, "a terminal ExpiredVoid row is never resurrected or re-touched");
        Suggestion stored = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == suggestionId));
        stored.State.Should().Be(SuggestionState.ExpiredVoid);
        stored.StateChangedAt.Should().Be(originalStamp);
    }

    [Fact]
    public async Task DriftAndExpire_ShouldNeverProduceABackwardTransition_UnderConcurrentWriters()
    {
        // The real point of the guarded compare-and-swap over an EF token: a row that is BOTH drifted AND past its
        // validity window, hit by drift's and expiry's writers CONCURRENTLY on real Postgres (two separate
        // connections, genuinely racing — not two sequential calls), must land ExpiredVoid regardless of which
        // writer's row lock wins, and must never observe a state going backward. Each writer's own prior-state
        // guard, re-evaluated against the committed row once the lock releases (Postgres READ COMMITTED), is what
        // makes the outcome deterministic despite the race: whichever runs second sees the first's committed write.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        DateTimeOffset expiresAt = Now.AddSeconds(-1); // already due
        decimal driftedPrice = Entry + Band + 1.00m;
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Active, expiresAt, instrument: "ES");

        // No self-check here (unlike the other tests' helper call): it would add an extra sequential DB round-trip
        // ahead of drift's real write and skew which writer's UPDATE reaches Postgres first, which is exactly the
        // ordering this test must NOT control.
        Task<int> driftTask = MarkDriftedStaleAsync("ES", OrderSide.Buy, bid: driftedPrice, ask: driftedPrice, now: Now);
        Task<int> expireTask = ExpireDueAsync(Now);
        int[] results = await Task.WhenAll(driftTask, expireTask);

        results.Sum().Should().BeGreaterThanOrEqualTo(1, "at least one writer must have acted — the anti-vacuity check");
        Suggestion stored = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == suggestionId));
        stored.State.Should().Be(
            SuggestionState.ExpiredVoid,
            "both drift-then-expire and expire-then-drift resolve to ExpiredVoid — drift can never un-stale a row expiry already voided, " +
            "and expiry's own IN (Active, Stale) guard still catches a row drift moved to Stale first");
        stored.StateChangedAt.Should().NotBeNull("whichever writer landed last stamped the audit trail");
    }

    // =============================================================================================================
    // 3. Cross-owner — background discovery bypasses R-20; a drifted suggestion is marked regardless of owner.
    // =============================================================================================================

    [Fact]
    public async Task Drift_ShouldMarkSuggestions_AcrossOwners_RegardlessOfUserId()
    {
        // The guarded update carries no owner filter at all (unlike a request-scoped, per-user query) — this is
        // what "the background discovery bypasses R-20" means concretely. Two rows, two DIFFERENT UserIds, same
        // instrument: one pass must mark BOTH, proving the write is owner-agnostic by construction rather than by
        // an accidentally-permissive filter that happens to admit one extra user.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid otherOwnerId = Guid.NewGuid(); // a fabricated "different owner" — no Users row needed (no FK on UserId)
        decimal driftedPrice = Entry + Band + 1.00m;

        Guid ownedByOperator = await SeedSuggestionAsync(
            accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Active, Now.AddHours(1), userId: operatorId, instrument: "ES-XOWNER");
        Guid ownedBySomeoneElse = await SeedSuggestionAsync(
            accountId, operatorId, OrderSide.Buy, Entry, SuggestionState.Active, Now.AddHours(1), userId: otherOwnerId, instrument: "ES-XOWNER");

        int marked = await MarkDriftedStaleAsync("ES-XOWNER", OrderSide.Buy, bid: driftedPrice, ask: driftedPrice, now: Now);

        marked.Should().Be(2, "one pass marks BOTH rows — drift is owner-agnostic, exactly like the expire sweep");
        (await StateOfAsync(ownedByOperator)).Should().Be(SuggestionState.Stale);
        (await StateOfAsync(ownedBySomeoneElse)).Should().Be(SuggestionState.Stale, "including the row owned by a user that isn't the caller");
    }

    // =============================================================================================================
    // 4. The symbol↔contract bridge — a quote for a resolved contract marks the suggestion under its neutral
    //    symbol; a spec miss or an unresolvable venue leaves it Active ("cannot measure ⇒ no transition").
    // =============================================================================================================

    [Fact]
    public async Task Bridge_ShouldMarkTheSuggestion_StoredUnderItsNeutralSymbol_WhenTheResolvedContractsQuoteArrives()
    {
        // Positive control for the two "cannot measure" cases below: CL is configured (tick 0.01, gh#541
        // defaults) and the adversarial venue resolves it to "CLM25" — a quote keyed by that CONTRACT must mark
        // the suggestion stored under the neutral SYMBOL "CL". This is the bridge, proven end to end.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, 70.00m, SuggestionState.Active, Now.AddHours(1), "CL");

        int marked = await ProcessSymbolQuoteAsync(symbol: "CL", contractKey: "CLM25", bid: 71.00m, ask: 71.00m);

        marked.Should().Be(1, "the resolved contract's quote reaches the suggestion stored under its neutral symbol");
        Suggestion stored = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == suggestionId));
        stored.State.Should().Be(SuggestionState.Stale);
        stored.Instrument.Should().Be("CL", "the row stays keyed by its neutral symbol — the bridge never rewrites it to the contract key");
    }

    [Fact]
    public async Task Bridge_ShouldLeaveActive_WhenTheInstrumentHasNoConfiguredSpec()
    {
        // "Cannot measure ⇒ do not transition" (R-4), half one: no configured InstrumentSpec means the drift band
        // can never be computed, so the symbol is skipped before the venue is even asked — MES is not among the
        // gh#541 defaults and nothing here configures it.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, 5_000.00m, SuggestionState.Active, Now.AddHours(1), "MES");

        int marked = await ProcessSymbolQuoteAsync(symbol: "MES", contractKey: "MESM25", bid: 5_500.00m, ask: 5_500.00m);

        marked.Should().Be(0, "an unconfigured instrument is never measured, however far the quote moved");
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Active);
    }

    [Fact]
    public async Task Bridge_ShouldLeaveActive_WhenTheVenueCannotResolveTheContract()
    {
        // "Cannot measure ⇒ do not transition", half two: NQ IS configured, but the venue seam is made
        // adversarially unable to resolve it this pass — the venue-round-trip failure the spec miss above cannot
        // exercise. The suggestion must stay Active exactly as it would for an unconfigured symbol.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, OrderSide.Buy, 18_000.00m, SuggestionState.Active, Now.AddHours(1), "NQ");
        VenueFactory.MakeResolveContractThrow("NQ");

        int marked = await ProcessSymbolQuoteAsync(symbol: "NQ", contractKey: "NQM25", bid: 19_000.00m, ask: 19_000.00m);

        marked.Should().Be(0, "a venue that cannot resolve the front-month contract leaves the suggestion Active");
        (await StateOfAsync(suggestionId)).Should().Be(SuggestionState.Active);
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory => _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Drives <see cref="ISuggestionDrift.MarkDriftedStaleAsync"/> directly — the guarded update in isolation,
    /// bypassing discovery and venue resolution entirely, so these tests exercise only the SQL predicate.</summary>
    private async Task<int> MarkDriftedStaleAsync(
        string instrument, OrderSide side, decimal bid, decimal ask, Guid? assertSuggestionExists = null, DateTimeOffset? now = null)
    {
        if (assertSuggestionExists is not null)
        {
            // A guard against a test author's own mistake: if the symbol here doesn't match what was seeded, every
            // "marked.Should().Be(0)" negative case would pass VACUOUSLY (nothing ever matched the WHERE clause).
            Suggestion seeded = await QueryDbAsync(db => db.Suggestions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == assertSuggestionExists));
            seeded.Instrument.Should().Be(instrument, "the test's own instrument argument must match what was seeded");
            seeded.Side.Should().Be(side);
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISuggestionDrift drift = scope.ServiceProvider.GetRequiredService<ISuggestionDrift>();
        // gh#718 changed the seam to return the transitioned (SuggestionId, UserId) rows; these tests assert on the
        // COUNT of the guarded update, so take .Count. (The exact rows recovered are proven by the gh#718 DB-tier test.)
        return (await drift.MarkDriftedStaleAsync(instrument, bid, ask, Band, now ?? Now, CancellationToken.None)).Count;
    }

    private async Task<int> ExpireDueAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISuggestionExpiry expiry = scope.ServiceProvider.GetRequiredService<ISuggestionExpiry>();
        return (await expiry.ExpireDueAsync(now, CancellationToken.None)).Count;
    }

    /// <summary>Drives <see cref="SuggestionDriftService.ProcessQuotesAsync"/> — discovery + resolve + the guarded
    /// update together, exactly as <see cref="SuggestionDriftHost"/> calls it after decoding a batch.</summary>
    private async Task<int> ProcessSymbolQuoteAsync(string symbol, string contractKey, decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SuggestionDriftService service = scope.ServiceProvider.GetRequiredService<SuggestionDriftService>();
        IProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();
        ITradingVenue venue = venueFactory.Create(FirmConventions.None);

        StopPromotionService.DecodedQuote quote = new(VenueId.Parse("projectx"), contractKey, bid, ask);
        return await service.ProcessQuotesAsync([quote], venue, Now, CancellationToken.None);
    }

    private Task<SuggestionState> StateOfAsync(Guid suggestionId) => QueryDbAsync(db => db.Suggestions
        .IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == suggestionId).Select(s => s.State).SingleAsync());

    private async Task<Guid> SeedSuggestionAsync(
        Guid accountId,
        Guid operatorId,
        OrderSide side,
        decimal entry,
        SuggestionState state,
        DateTimeOffset expiresAt,
        string instrument = "ES",
        DateTimeOffset? stateChangedAt = null,
        Guid? userId = null)
    {
        Guid id = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Origin = SuggestionOrigin.Scan,
                Id = id,
                UserId = userId ?? operatorId,
                AccountId = accountId,
                Instrument = instrument,
                Side = side,
                Size = 1,
                EntryPrice = entry,
                StopPrice = side == OrderSide.Buy ? entry - 10m : entry + 10m,
                TargetPrice = side == OrderSide.Buy ? entry + 20m : entry - 20m,
                Mode = TradingMode.Practice,
                State = state,
                CreatedAt = Now.AddHours(-1),
                Rationale = "seeded for the drift guard suite (gh#632)",
                CitedFactors =
                [
                    new CitedFactor
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId ?? operatorId,
                        Kind = CitedFactorKind.Indicator,
                        IsPrimary = true,
                        TimeframeMinutes = 5,
                        Indicator = "rsi",
                        Period = 14,
                    },
                ],
                Confidence = 60,
                ExpiresAt = expiresAt,
                StateChangedAt = stateChangedAt,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered tradeable account, and
    /// wipes the suggestion journal first — the same shape as <c>SuggestionDatabaseGuardIntegrationTests</c>, so
    /// every test starts from a clean slate regardless of instrument reuse across tests.</summary>
    private async Task<(Guid AccountId, Guid OperatorId)> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.First(a => a.CanTrade);
        Guid operatorId = await QueryDbAsync(db => db.Users.AsNoTracking().Where(u => u.Email == PostgresApiFactory.OperatorEmail).Select(u => u.Id).SingleAsync());
        return (account.Id, operatorId);
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
