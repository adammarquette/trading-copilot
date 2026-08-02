using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Suggestions;

/// <summary>
/// The suggestion read model (gh#540, R-4): the <b>first</b> way an operator can see a suggestion at all — the
/// agent-review route (gh#402) has been writing rows since it shipped and the only production reader was a
/// <c>CountAsync</c>.
/// </summary>
/// <remarks>
/// The properties that matter: the list surfaces the <b>actionable</b> rows by default and newest-first (a stale or
/// void suggestion must not sit at the top of a decision surface); the page size is capped so a client cannot ask for
/// the whole journal; get-by-id returns <b>any</b> state so the journal stays readable after a row expires; the
/// reward:risk ratio is derived server-side and refuses to divide by zero; and every read is R-20-scoped by the
/// DbContext filter — another operator's suggestion is invisible, not merely filtered from a list.
/// </remarks>
public class SuggestionEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly IOptions<SuggestionOptions> _options = Options.Create(new SuggestionOptions());
    private static readonly DateTimeOffset _t = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static SuggestionListResponse ListOf(IResult result) =>
        (SuggestionListResponse)((IValueHttpResult)result).Value!;

    private static SuggestionResponse ItemOf(IResult result) =>
        (SuggestionResponse)((IValueHttpResult)result).Value!;

    private async Task<Guid> SeedAsync(
        SuggestionState state = SuggestionState.Active,
        DateTimeOffset? createdAt = null,
        Guid? owner = null,
        Guid? accountId = null,
        OrderSide side = OrderSide.Buy,
        decimal entry = 100m,
        decimal stop = 99m,
        decimal target = 103m)
    {
        Guid id = Guid.NewGuid();
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Suggestions.Add(new Suggestion
        {
            Id = id,
            UserId = ownerId,
            AccountId = accountId ?? _account,
            Instrument = "ES",
            Side = side,
            Size = 2,
            EntryPrice = entry,
            StopPrice = stop,
            TargetPrice = target,
            Mode = TradingMode.Practice,
            State = state,
            CreatedAt = createdAt ?? _t,
            Rationale = "oversold bounce",
            CitedIndicator = "rsi",
            CitedPeriod = 14,
            CitedResolutionMinutes = 1,
            Confidence = 72,
            ExpiresAt = (createdAt ?? _t).AddMinutes(60),
        });
        await context.SaveChangesAsync();
        return id;
    }

    // The real configuration-backed source, so the dollar figures are exercised against the shipped built-in specs
    // (gh#541) rather than a fake that could agree with a wrong implementation.
    private static readonly IInstrumentSpecSource _instrumentSpecs =
        new InstrumentSpecSource(Options.Create(new InstrumentSpecOptions()));

    private async Task<IResult> ListAsync(
        Guid? accountId = null,
        SuggestionState? state = null,
        int? limit = null,
        Guid? asUser = null,
        SuggestionOptions? readOptions = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.ListAsync(
            accountId ?? _account, state, limit, context,
            readOptions is null ? _options : Options.Create(readOptions), _instrumentSpecs, default);
    }

    private async Task<IResult> GetAsync(Guid id, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.GetAsync(id, context, _instrumentSpecs, default);
    }

    // ---- list: default is the actionable set, newest first ----

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyActive_ByDefault()
    {
        Guid active = await SeedAsync(SuggestionState.Active);
        await SeedAsync(SuggestionState.Stale);
        await SeedAsync(SuggestionState.ExpiredVoid);

        SuggestionListResponse list = ListOf(await ListAsync());

        // A decision surface defaults to what can still be acted on; the rest stays reachable by id and by filter.
        list.Items.Should().ContainSingle().Which.Id.Should().Be(active);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnNewestFirst()
    {
        Guid older = await SeedAsync(createdAt: _t.AddMinutes(-10));
        Guid newer = await SeedAsync(createdAt: _t);

        SuggestionListResponse list = ListOf(await ListAsync());

        list.Items.Select(item => item.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnTheRequestedState_WhenOneIsGiven()
    {
        await SeedAsync(SuggestionState.Active);
        Guid stale = await SeedAsync(SuggestionState.Stale);

        SuggestionListResponse list = ListOf(await ListAsync(state: SuggestionState.Stale));

        list.Items.Should().ContainSingle().Which.Id.Should().Be(stale);
    }

    [Fact]
    public async Task ListAsync_ShouldOnlyReturnTheRequestedAccount()
    {
        Guid mine = await SeedAsync();
        Guid otherAccount = Guid.NewGuid();
        await SeedAsync(accountId: otherAccount);

        SuggestionListResponse list = ListOf(await ListAsync());

        list.Items.Should().ContainSingle().Which.Id.Should().Be(mine);
    }

    [Fact]
    public async Task ListAsync_ShouldCapThePageSize_WhenAnOversizedLimitIsAsked()
    {
        // Seed MORE than the cap, so the assertion can only pass if the clamp actually binds. Asserting
        // "count <= cap" against fewer rows than the cap would hold whether or not the clamp exists.
        SuggestionOptions tight = new() { DefaultPageSize = 2, MaxPageSize = 3 };
        for (int i = 0; i < 5; i++)
        {
            await SeedAsync(createdAt: _t.AddMinutes(-i));
        }

        // A client cannot pull the whole journal in one call, however large a limit it asks for.
        SuggestionListResponse list = ListOf(await ListAsync(limit: int.MaxValue, readOptions: tight));

        list.Items.Count.Should().Be(tight.MaxPageSize);
    }

    [Fact]
    public async Task ListAsync_ShouldUseTheConfiguredDefault_WhenNoLimitIsGiven()
    {
        SuggestionOptions tight = new() { DefaultPageSize = 2, MaxPageSize = 3 };
        for (int i = 0; i < 5; i++)
        {
            await SeedAsync(createdAt: _t.AddMinutes(-i));
        }

        SuggestionListResponse list = ListOf(await ListAsync(readOptions: tight));

        list.Items.Count.Should().Be(tight.DefaultPageSize);
    }

    [Fact]
    public async Task ListAsync_ShouldRejectANonPositiveLimit()
    {
        await SeedAsync();

        StatusOf(await ListAsync(limit: 0)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ---- R-20: another operator's suggestion is invisible, not merely absent from a list ----

    [Fact]
    public async Task ListAsync_ShouldReturnNothing_ForAnotherOperator()
    {
        await SeedAsync(owner: _operator);

        SuggestionListResponse list = ListOf(await ListAsync(asUser: _other));

        list.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNotFound_ForAnotherOperatorsSuggestion()
    {
        Guid mine = await SeedAsync(owner: _operator);

        // 404 rather than 403: the row's existence is not disclosed to a stranger.
        StatusOf(await GetAsync(mine, asUser: _other)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---- get by id: any state, so the journal stays readable ----

    [Theory]
    [InlineData(SuggestionState.Active)]
    [InlineData(SuggestionState.Stale)]
    [InlineData(SuggestionState.ExpiredVoid)]
    public async Task GetAsync_ShouldReturnTheSuggestion_InAnyState(SuggestionState state)
    {
        Guid id = await SeedAsync(state);

        ItemOf(await GetAsync(id)).Id.Should().Be(id);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNotFound_ForAnUnknownId()
    {
        StatusOf(await GetAsync(Guid.NewGuid())).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---- the derived reward:risk ratio ----

    [Fact]
    public async Task GetAsync_ShouldDeriveTheRewardRiskRatio_ForALong()
    {
        // entry 100, stop 99, target 103 -> risk 1, reward 3 -> 3.0R
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 100m, stop: 99m, target: 103m);

        ItemOf(await GetAsync(id)).RewardRiskRatio.Should().Be(3m);
    }

    [Fact]
    public async Task GetAsync_ShouldDeriveTheRewardRiskRatio_ForAShort()
    {
        // A short's geometry is inverted; the ratio is a magnitude and must come out positive either way.
        Guid id = await SeedAsync(side: OrderSide.Sell, entry: 100m, stop: 101m, target: 97m);

        ItemOf(await GetAsync(id)).RewardRiskRatio.Should().Be(3m);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnANullRatio_WhenTheStopEqualsTheEntry()
    {
        // Zero risk would divide by zero. Geometry is validated at issuance, but the read model must never throw on a
        // row it did not write -- absent is the honest answer, not infinity.
        Guid id = await SeedAsync(entry: 100m, stop: 100m, target: 103m);

        ItemOf(await GetAsync(id)).RewardRiskRatio.Should().BeNull();
    }

    // ---- dollar risk/reward from the instrument spec (gh#541) ----

    [Fact]
    public async Task GetAsync_ShouldMoneyValueTheGeometry_WhenTheInstrumentHasASpec()
    {
        // ES: $50 a point. Risk 1 point x 2 contracts = $100; reward 3 points x 2 = $300.
        Guid id = await SeedAsync(entry: 100m, stop: 99m, target: 103m);

        SuggestionResponse item = ItemOf(await GetAsync(id));

        item.RiskUsd.Should().Be(100m);
        item.RewardUsd.Should().Be(300m);
    }

    [Fact]
    public async Task GetAsync_ShouldMoneyValueSymmetrically_ForAShort()
    {
        // The money math is direction-agnostic: an inverted short geometry values identically.
        Guid id = await SeedAsync(side: OrderSide.Sell, entry: 100m, stop: 101m, target: 97m);

        SuggestionResponse item = ItemOf(await GetAsync(id));

        item.RiskUsd.Should().Be(100m);
        item.RewardUsd.Should().Be(300m);
    }

    [Fact]
    public async Task GetAsync_ShouldOmitTheDollarFigures_WhenTheInstrumentHasNoSpec()
    {
        // An unconfigured instrument yields no dollar figures rather than a guessed tick size. This is a DISPLAY
        // concern, so the read degrades; the take path is where a missing spec must refuse outright (gh#548).
        Guid id = await SeedAsync();
        await using (TradingCopilotDbContext seed = Context())
        {
            Suggestion row = await seed.Suggestions.SingleAsync(s => s.Id == id);
            row.Instrument = "ZZ";
            await seed.SaveChangesAsync();
        }

        SuggestionResponse item = ItemOf(await GetAsync(id));

        item.RiskUsd.Should().BeNull();
        item.RewardUsd.Should().BeNull();
        item.RewardRiskRatio.Should().NotBeNull("the unit-free ratio needs no contract spec");
    }

    [Fact]
    public async Task GetAsync_ShouldProjectTheSpineFaithfully()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 100.25m, stop: 99.75m, target: 101.75m);

        SuggestionResponse item = ItemOf(await GetAsync(id));

        item.AccountId.Should().Be(_account);
        item.Instrument.Should().Be("ES");
        item.Side.Should().Be(OrderSide.Buy);
        item.Size.Should().Be(2);
        item.EntryPrice.Should().Be(100.25m);
        item.StopPrice.Should().Be(99.75m);
        item.TargetPrice.Should().Be(101.75m);
        item.Mode.Should().Be(TradingMode.Practice);
        item.State.Should().Be(SuggestionState.Active);
        item.CreatedAt.Should().Be(_t);
        item.TimeframeMinutes.Should().Be(1, "the seed cites a 1-minute indicator");
    }

    [Fact]
    public async Task GetAsync_ShouldSurfaceTheTimeframe_AsTheCitedResolution()
    {
        // gh#592: a suggestion's headline timeframe IS its cited indicator's resolution in the single-signal model
        // that exists today — a first-class attribute, not indicator provenance, with no duplicate source of truth.
        Guid id = await SeedAsync();

        SuggestionResponse item = ItemOf(await GetAsync(id));

        item.TimeframeMinutes.Should().Be(item.CitedResolutionMinutes);
    }

    // ---- pass: record a neutral operator disposition (gh#547) ----

    private async Task<IResult> PassAsync(Guid id, SuggestionPassRequest? request = null, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.PassAsync(id, request, context, default);
    }

    private static SuggestionDispositionResponse DispositionOf(IResult result) =>
        (SuggestionDispositionResponse)((IValueHttpResult)result).Value!;

    private async Task<int> DispositionCountAsync(Guid suggestionId, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await context.SuggestionDispositions.CountAsync(disposition => disposition.SuggestionId == suggestionId);
    }

    [Fact]
    public async Task PassAsync_ShouldRecordANeutralPass_WhenNoReasonIsGiven()
    {
        Guid id = await SeedAsync();

        IResult result = await PassAsync(id);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        SuggestionDispositionResponse disposition = DispositionOf(result);
        disposition.Kind.Should().Be(SuggestionDispositionKind.Passed);
        disposition.Reasons.Should().Be(SuggestionPassReason.None, "a pass is a neutral decline; a reason is optional");
        disposition.Note.Should().BeNull();
        (await DispositionCountAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task PassAsync_ShouldRecordTheReasonsAndNote_WhenGiven()
    {
        Guid id = await SeedAsync();
        SuggestionPassReason reasons = SuggestionPassReason.NewsRisk | SuggestionPassReason.WrongTime;

        SuggestionDispositionResponse disposition =
            DispositionOf(await PassAsync(id, new SuggestionPassRequest(reasons, "  waiting for the number  ")));

        disposition.Reasons.Should().Be(reasons);
        disposition.Note.Should().Be("waiting for the number", "the note is trimmed");
    }

    [Fact]
    public async Task PassAsync_ShouldConflict_OnASecondPass()
    {
        Guid id = await SeedAsync();
        await PassAsync(id);

        // Append-only journal evidence: a second disposition conflicts rather than overwriting.
        StatusOf(await PassAsync(id, new SuggestionPassRequest(SuggestionPassReason.Sizing)))
            .Should().Be(StatusCodes.Status409Conflict);
        (await DispositionCountAsync(id)).Should().Be(1, "the second pass must not add or overwrite a row");
    }

    [Theory]
    [InlineData(SuggestionState.Stale)]
    [InlineData(SuggestionState.ExpiredVoid)]
    public async Task PassAsync_ShouldBeAccepted_OnAStaleOrExpiredSuggestion(SuggestionState state)
    {
        // The operator's note is worth keeping even past the window; lifecycle is the clock's, not the pass's (gh#539).
        Guid id = await SeedAsync(state);

        StatusOf(await PassAsync(id)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task PassAsync_ShouldReturnNotFound_ForAnotherOperatorsSuggestion()
    {
        Guid mine = await SeedAsync(owner: _operator);

        // 404, not 403: a stranger is never told the row exists, and cannot write to it.
        StatusOf(await PassAsync(mine, asUser: _other)).Should().Be(StatusCodes.Status404NotFound);
        (await DispositionCountAsync(mine)).Should().Be(0);
    }

    [Fact]
    public async Task PassAsync_ShouldReturnNotFound_ForAnUnknownId()
    {
        StatusOf(await PassAsync(Guid.NewGuid())).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task PassAsync_ShouldRejectUnrecognisedReasonBits()
    {
        Guid id = await SeedAsync();
        // A bit outside the canonical eight is garbage, not a reason -- fail closed.
        SuggestionPassReason bogus = (SuggestionPassReason)(1 << 20);

        StatusOf(await PassAsync(id, new SuggestionPassRequest(bogus))).Should().Be(StatusCodes.Status400BadRequest);
        (await DispositionCountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task PassAsync_ShouldRejectAnOverlongNote()
    {
        Guid id = await SeedAsync();
        string tooLong = new('x', SuggestionDisposition.NoteMaxLength + 1);

        StatusOf(await PassAsync(id, new SuggestionPassRequest(Note: tooLong))).Should().Be(StatusCodes.Status400BadRequest);
        (await DispositionCountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task PassAsync_ShouldTreatAWhitespaceNoteAsNone()
    {
        Guid id = await SeedAsync();

        DispositionOf(await PassAsync(id, new SuggestionPassRequest(Note: "   "))).Note.Should().BeNull();
    }

    [Fact]
    public async Task PassAsync_ShouldNotMutateTheSuggestionsTradeParameters()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 100m, stop: 99m, target: 103m);

        await PassAsync(id, new SuggestionPassRequest(SuggestionPassReason.WeakRewardRisk, "note"));

        await using TradingCopilotDbContext context = Context();
        Suggestion after = await context.Suggestions.SingleAsync(suggestion => suggestion.Id == id);
        after.EntryPrice.Should().Be(100m);
        after.StopPrice.Should().Be(99m);
        after.TargetPrice.Should().Be(103m);
        after.Size.Should().Be(2);
        after.State.Should().Be(SuggestionState.Active, "a disposition never moves lifecycle state");
    }

    [Fact]
    public async Task PassAsync_DispositionShouldSurvive_WhenTheSuggestionLaterExpires()
    {
        // The expiry sweep (gh#545) advances State on a SEPARATE table; it can never overwrite or erase a disposition.
        Guid id = await SeedAsync();
        await PassAsync(id, new SuggestionPassRequest(SuggestionPassReason.LowConviction, "keep me"));

        await using (TradingCopilotDbContext sweep = Context())
        {
            Suggestion row = await sweep.Suggestions.SingleAsync(suggestion => suggestion.Id == id);
            row.State = SuggestionState.ExpiredVoid;
            await sweep.SaveChangesAsync();
        }

        await using TradingCopilotDbContext check = Context();
        SuggestionDisposition disposition = await check.SuggestionDispositions.SingleAsync(d => d.SuggestionId == id);
        disposition.Kind.Should().Be(SuggestionDispositionKind.Passed);
        disposition.Reasons.Should().Be(SuggestionPassReason.LowConviction);
        disposition.Note.Should().Be("keep me");
    }

    // ---- the disposition's effect on the read model ----

    [Fact]
    public async Task ListAsync_ShouldExcludeADispositionedSuggestion_FromTheDefaultActionableList()
    {
        Guid passed = await SeedAsync();
        Guid live = await SeedAsync(createdAt: _t.AddMinutes(-5));
        await PassAsync(passed);

        SuggestionListResponse list = ListOf(await ListAsync());

        // The decision surface hides what has been acted on; the still-live one remains.
        list.Items.Select(item => item.Id).Should().Equal(live);
    }

    [Fact]
    public async Task GetAsync_ShouldStillReturnADispositionedSuggestion_ById()
    {
        Guid id = await SeedAsync();
        await PassAsync(id);

        // The journal stays readable by id even after the operator has passed.
        ItemOf(await GetAsync(id)).Id.Should().Be(id);
    }

    [Fact]
    public async Task ListAsync_ShouldStillReturnADispositionedSuggestion_UnderAnExplicitStateFilter()
    {
        Guid id = await SeedAsync(SuggestionState.Active);
        await PassAsync(id);

        // An explicit state query is a literal/historical view, not the default actionable surface.
        SuggestionListResponse list = ListOf(await ListAsync(state: SuggestionState.Active));
        list.Items.Select(item => item.Id).Should().Contain(id);
    }
}
