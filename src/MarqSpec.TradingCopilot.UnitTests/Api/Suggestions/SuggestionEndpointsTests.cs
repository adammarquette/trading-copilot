using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
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
    private static readonly IOptions<SuggestionReadOptions> _options = Options.Create(new SuggestionReadOptions());
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
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<IResult> ListAsync(
        Guid? accountId = null,
        SuggestionState? state = null,
        int? limit = null,
        Guid? asUser = null,
        SuggestionReadOptions? readOptions = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.ListAsync(
            accountId ?? _account, state, limit, context, readOptions is null ? _options : Options.Create(readOptions), default);
    }

    private async Task<IResult> GetAsync(Guid id, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.GetAsync(id, context, default);
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
        SuggestionReadOptions tight = new() { DefaultPageSize = 2, MaxPageSize = 3 };
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
        SuggestionReadOptions tight = new() { DefaultPageSize = 2, MaxPageSize = 3 };
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
    }
}
