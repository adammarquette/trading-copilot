using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Journal;

/// <summary>
/// The <c>/trades/{tradeId}/feedback</c> read + write surface (gh#1064, R-8). The behaviours that matter: a foreign
/// or absent trade is a <b>404</b> (R-20); feedback can only attach to a <b>closed</b> trade; an empty submission is
/// refused; "awaiting review" is <b>derived</b> — true for a closed trade with no operator feedback, false once the
/// operator has left any, and never true for a trade that has not closed yet — and none of this touches the trade
/// row itself (the close path stays untouched, gh#289).
/// </summary>
public class TradeFeedbackEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly DateTimeOffset _now = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private async Task<Guid> SeedTradeAsync(Guid? owner = null, DateTimeOffset? closedAt = null)
    {
        Guid id = Guid.NewGuid();
        Guid ownerId = owner ?? _operator;

        await using TradingCopilotDbContext context = Context(ownerId);
        context.Trades.Add(new Trade
        {
            Id = id,
            UserId = ownerId,
            AccountId = Guid.NewGuid(),
            Instrument = "CON.F.US.ES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_300m,
            ExitPrice = closedAt is null ? null : 5_305m,
            RealizedPnL = closedAt is null ? null : 250m,
            Mode = TradingMode.Practice,
            ClosedAt = closedAt,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private Task<Guid> SeedClosedTradeAsync(Guid? owner = null) => SeedTradeAsync(owner, closedAt: _now.AddMinutes(-5));

    // -- GET: read + the derived awaiting-review flag --------------------------------------------------------------

    [Fact]
    public async Task GetFeedbackAsync_ShouldReturnNotFound_ForAnAbsentTrade()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(Guid.NewGuid(), context, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetFeedbackAsync_ShouldReturnNotFound_ForAForeignTrade()
    {
        Guid tradeId = await SeedClosedTradeAsync(owner: Guid.NewGuid()); // a stranger's trade

        await using TradingCopilotDbContext caller = Context(); // a different operator
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(tradeId, caller, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound); // R-20 -- never a disclosure
    }

    [Fact]
    public async Task GetFeedbackAsync_ShouldReportAwaitingReview_ForAClosedTradeWithNoFeedback()
    {
        Guid tradeId = await SeedClosedTradeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(tradeId, context, default);

        TradeFeedbackSummaryResponse summary = ValueOf<TradeFeedbackSummaryResponse>(result);
        summary.TradeId.Should().Be(tradeId);
        summary.AwaitingReview.Should().BeTrue();
        summary.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFeedbackAsync_ShouldNotReportAwaitingReview_ForAnOpenTrade()
    {
        // An open trade has nothing to review yet -- never "awaiting", even though it also carries no feedback.
        Guid tradeId = await SeedTradeAsync(closedAt: null);

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(tradeId, context, default);

        ValueOf<TradeFeedbackSummaryResponse>(result).AwaitingReview.Should().BeFalse();
    }

    [Fact]
    public async Task GetFeedbackAsync_ShouldNotReportAwaitingReview_OnceOperatorFeedbackExists()
    {
        Guid tradeId = await SeedClosedTradeAsync();
        await using (TradingCopilotDbContext context = Context())
        {
            await TradeFeedbackEndpoints.AddFeedbackAsync(
                tradeId, new AddTradeFeedbackRequest("Good discipline", null, null), _now, context, default);
        }

        await using TradingCopilotDbContext verify = Context();
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(tradeId, verify, default);

        TradeFeedbackSummaryResponse summary = ValueOf<TradeFeedbackSummaryResponse>(result);
        summary.AwaitingReview.Should().BeFalse();
        summary.Entries.Should().ContainSingle().Which.Comment.Should().Be("Good discipline");
    }

    [Fact]
    public async Task GetFeedbackAsync_ShouldReturnEntriesOldestFirst()
    {
        Guid tradeId = await SeedClosedTradeAsync();
        await using (TradingCopilotDbContext context = Context())
        {
            await TradeFeedbackEndpoints.AddFeedbackAsync(
                tradeId, new AddTradeFeedbackRequest("first", null, null), _now, context, default);
        }
        await using (TradingCopilotDbContext context = Context())
        {
            await TradeFeedbackEndpoints.AddFeedbackAsync(
                tradeId, new AddTradeFeedbackRequest("second", null, null), _now.AddMinutes(1), context, default);
        }

        await using TradingCopilotDbContext verify = Context();
        IResult result = await TradeFeedbackEndpoints.GetFeedbackAsync(tradeId, verify, default);

        ValueOf<TradeFeedbackSummaryResponse>(result).Entries.Select(entry => entry.Comment)
            .Should().ContainInOrder("first", "second"); // asynchronous -- feedback can be added anytime (R-8)
    }

    // -- POST: attach feedback --------------------------------------------------------------------------------------

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnNotFound_ForAnAbsentTrade()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            Guid.NewGuid(), new AddTradeFeedbackRequest("note", null, null), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnNotFound_ForAForeignTrade_AndWriteNothing()
    {
        Guid tradeId = await SeedClosedTradeAsync(owner: Guid.NewGuid());

        await using TradingCopilotDbContext caller = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest("sneaky", null, null), _now, caller, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);

        await using TradingCopilotDbContext asOwner = Context(); // still the caller's own (empty) view
        (await asOwner.TradeFeedbacks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnBadRequest_WhenTheTradeHasNotClosedYet()
    {
        Guid tradeId = await SeedTradeAsync(closedAt: null);

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest("too soon", null, null), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnBadRequest_WhenNothingIsSubmitted()
    {
        Guid tradeId = await SeedClosedTradeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest(null, null, null), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnBadRequest_WhenOnlyWhitespaceIsSubmitted()
    {
        Guid tradeId = await SeedClosedTradeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest("   ", [], "  "), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnBadRequest_WhenCommentExceedsMaxLength()
    {
        Guid tradeId = await SeedClosedTradeAsync();
        string tooLong = new('x', TradeFeedback.CommentMaxLength + 1);

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest(tooLong, null, null), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldReturnBadRequest_WhenEmotionalStateExceedsMaxLength()
    {
        Guid tradeId = await SeedClosedTradeAsync();
        string tooLong = new('x', TradeFeedback.EmotionalStateMaxLength + 1);

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest(null, null, tooLong), _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldPersistTheEntry_AuthoredByTheOperator_WithTheInjectedClock()
    {
        Guid tradeId = await SeedClosedTradeAsync();

        await using TradingCopilotDbContext context = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest(" Held too long ", ["fomo", "fomo", " late-entry "], " Anxious "),
            _now, context, default);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
        TradeFeedbackEntryResponse response = ValueOf<TradeFeedbackEntryResponse>(result);
        response.Comment.Should().Be("Held too long"); // trimmed
        response.Tags.Should().BeEquivalentTo(["fomo", "late-entry"]); // trimmed + de-duplicated
        response.EmotionalState.Should().Be("Anxious");
        response.Author.Should().Be(nameof(FeedbackAuthor.Operator));
        response.CreatedAt.Should().Be(_now);

        await using TradingCopilotDbContext verify = Context();
        TradeFeedback stored = await verify.TradeFeedbacks.SingleAsync();
        stored.TradeId.Should().Be(tradeId);
        stored.UserId.Should().Be(_operator);
        stored.Author.Should().Be(FeedbackAuthor.Operator);
    }

    [Fact]
    public async Task AddFeedbackAsync_ShouldAllowASecondEntry_OnTheSameTrade_Asynchronously()
    {
        Guid tradeId = await SeedClosedTradeAsync();
        await using (TradingCopilotDbContext context = Context())
        {
            await TradeFeedbackEndpoints.AddFeedbackAsync(
                tradeId, new AddTradeFeedbackRequest("first pass", null, null), _now, context, default);
        }

        await using TradingCopilotDbContext second = Context();
        IResult result = await TradeFeedbackEndpoints.AddFeedbackAsync(
            tradeId, new AddTradeFeedbackRequest("a week later", null, null), _now.AddDays(7), second, default);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);

        await using TradingCopilotDbContext verify = Context();
        (await verify.TradeFeedbacks.CountAsync(f => f.TradeId == tradeId)).Should().Be(2);
    }
}
