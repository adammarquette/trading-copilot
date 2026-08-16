using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The <c>/api/ai/attribution</c> handler (gh#767 increment B) — per-suggestion and per-taken-trade AI cost, read from
/// the durable <c>AIUsage</c> ledger's firing correlation (gh#767 increment A), owner-scoped (R-20). A suggestion's
/// cost is the sum of its firing's rows (triage + any escalated deep); a taken trade reports the cost of the suggestion
/// that produced it (via <c>Trade.SuggestionId → Suggestion.TriggerFiringId</c>); and billed reviews that staged no
/// suggestion are surfaced as an <b>unattributed</b> total, never silently dropped.
/// </summary>
public class AiAttributionEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static DateTimeOffset Now => new(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset MonthStart => new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task SeedCostAsync(Guid owner, Guid firing, decimal cost, LlmModelTier tier = LlmModelTier.Triage)
    {
        await using TradingCopilotDbContext context = Context(owner);
        context.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Feature = AiUsageFeature.Triage,
            Model = "claude-haiku-4-5",
            Tier = tier,
            Outcome = AiUsageOutcome.Succeeded,
            EstimatedCostUsd = cost,
            TriggerFiringId = firing,
            OccurredAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedSuggestionAsync(Guid owner, Guid firing, string instrument, DateTimeOffset createdAt)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(owner);
        context.Suggestions.Add(new Suggestion
        {
            Id = id,
            UserId = owner,
            AccountId = Guid.NewGuid(),
            Instrument = instrument,
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 100m,
            StopPrice = 99m,
            TargetPrice = 103m,
            Mode = TradingMode.Practice,
            State = SuggestionState.Active,
            CreatedAt = createdAt,
            Rationale = "oversold",
            CitedIndicator = "RSI",
            CitedPeriod = 14,
            CitedResolutionMinutes = 5,
            Confidence = 70,
            ExpiresAt = createdAt.AddHours(1),
            TriggerFiringId = firing,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task SeedTradeAsync(Guid owner, Guid suggestionId, string instrument, decimal pnl, DateTimeOffset closedAt)
    {
        await using TradingCopilotDbContext context = Context(owner);
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AccountId = Guid.NewGuid(),
            Instrument = instrument,
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 100m,
            ExitPrice = 100m + pnl,
            RealizedPnL = pnl,
            Mode = TradingMode.Practice,
            ClosedAt = closedAt,
            SuggestionId = suggestionId,
        });
        await context.SaveChangesAsync();
    }

    private static AiAttributionResponse Body(IResult result) => (AiAttributionResponse)((IValueHttpResult)result).Value!;

    [Fact]
    public async Task GetAttributionAsync_ShouldReportEachSuggestionsTotalCost_SummedOverItsFiring()
    {
        Guid firing = Guid.NewGuid();
        Guid suggestionId = await SeedSuggestionAsync(_operator, firing, "ES", Now);
        await SeedCostAsync(_operator, firing, 0.0005m);                        // triage
        await SeedCostAsync(_operator, firing, 0.0072m, LlmModelTier.Deep);     // escalated deep

        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            MonthStart, Now, Now, Context(), CancellationToken.None);

        AiSuggestionCost cost = Body(result).Suggestions.Should().ContainSingle().Subject;
        cost.SuggestionId.Should().Be(suggestionId);
        cost.Instrument.Should().Be("ES");
        cost.CostUsd.Should().Be(0.0077m);   // triage + deep, summed over the firing
        cost.Calls.Should().Be(2);
        cost.Escalated.Should().BeTrue();    // two billed calls == a triage→deep escalation
    }

    [Fact]
    public async Task GetAttributionAsync_ShouldReportATakenTradesCost_ViaItsSuggestionsFiring()
    {
        Guid firing = Guid.NewGuid();
        Guid suggestionId = await SeedSuggestionAsync(_operator, firing, "NQ", Now);
        await SeedCostAsync(_operator, firing, 0.0060m);
        await SeedTradeAsync(_operator, suggestionId, "NQ", 125m, Now);

        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            MonthStart, Now, Now, Context(), CancellationToken.None);

        AiTradeCost trade = Body(result).TakenTrades.Should().ContainSingle().Subject;
        trade.Instrument.Should().Be("NQ");
        trade.RealizedPnL.Should().Be(125m);
        trade.SuggestionCostUsd.Should().Be(0.0060m); // the cost of the suggestion that produced the trade
    }

    [Fact]
    public async Task GetAttributionAsync_ShouldBucketOrphanFiringCost_AsUnattributed_NotDropIt()
    {
        // A billed review that staged NO suggestion (suppressed / geometry-rejected / throttled) leaves cost rows
        // whose firing no suggestion references — surfaced as unattributed, never silently dropped (gh#767 review).
        Guid orphanFiring = Guid.NewGuid();
        await SeedCostAsync(_operator, orphanFiring, 0.0004m);

        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            MonthStart, Now, Now, Context(), CancellationToken.None);

        AiAttributionResponse body = Body(result);
        body.Suggestions.Should().BeEmpty();
        body.UnattributedUsd.Should().Be(0.0004m);
    }

    [Fact]
    public async Task GetAttributionAsync_ShouldScopeToTheOwner()
    {
        Guid myFiring = Guid.NewGuid();
        Guid mySuggestion = await SeedSuggestionAsync(_operator, myFiring, "ES", Now);
        await SeedCostAsync(_operator, myFiring, 0.0005m);

        Guid theirs = Guid.NewGuid();
        Guid theirFiring = Guid.NewGuid();
        await SeedSuggestionAsync(theirs, theirFiring, "CL", Now);
        await SeedCostAsync(theirs, theirFiring, 9.99m);

        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            MonthStart, Now, Now, Context(), CancellationToken.None);

        AiAttributionResponse body = Body(result);
        body.Suggestions.Should().ContainSingle().Which.SuggestionId.Should().Be(mySuggestion);
        body.UnattributedUsd.Should().Be(0m); // the other operator's spend is invisible (R-20)
    }

    [Fact]
    public async Task GetAttributionAsync_ShouldReturnEmpty_WhenNothingInThePeriod()
    {
        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            MonthStart, Now, Now, Context(), CancellationToken.None);

        AiAttributionResponse body = Body(result);
        body.Suggestions.Should().BeEmpty();
        body.TakenTrades.Should().BeEmpty();
        body.UnattributedUsd.Should().Be(0m);
    }

    [Fact]
    public async Task GetAttributionAsync_ShouldRejectAnInvertedPeriod()
    {
        IResult result = await AiAttributionEndpoints.GetAttributionAsync(
            Now, MonthStart, Now, Context(), CancellationToken.None); // from after to

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
