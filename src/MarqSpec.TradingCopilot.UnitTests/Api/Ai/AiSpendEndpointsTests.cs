using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The <c>/api/ai/spend</c> handler (gh#741) — the operator's own AI spend, read from the durable <c>AIUsage</c>
/// ledger (never the export-only meter, ADR-0002), owner-scoped (R-20, ADR-0015). Aggregates total / by model / by
/// Central trading day over a period, plus today's spend against the governor's daily cap.
/// </summary>
public class AiSpendEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    // 2026-07-15 18:00Z == 13:00 CDT on the 15th. The Central day is the 15th, whose midnight is 05:00Z.
    private static DateTimeOffset Now => new(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset MonthStart => new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task SeedAsync(Guid owner, string model, decimal cost, DateTimeOffset occurredAt)
    {
        await using TradingCopilotDbContext context = Context(owner);
        context.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Model = model,
            EstimatedCostUsd = cost,
            OccurredAt = occurredAt,
        });
        await context.SaveChangesAsync();
    }

    private static AiSpendResponse Body(IResult result) => (AiSpendResponse)((IValueHttpResult)result).Value!;

    [Fact]
    public async Task GetSpendAsync_ShouldAggregateTotalByModelAndByDay_OverThePeriod()
    {
        await SeedAsync(_operator, "claude-haiku-4-5", 0.10m, new DateTimeOffset(2026, 7, 14, 18, 0, 0, TimeSpan.Zero)); // 13:00 CDT, 14th
        await SeedAsync(_operator, "claude-haiku-4-5", 0.20m, new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero)); // 11:00 CDT, 15th
        await SeedAsync(_operator, "claude-opus-5", 1.00m, new DateTimeOffset(2026, 7, 15, 17, 0, 0, TimeSpan.Zero)); // 12:00 CDT, 15th

        IResult result = await AiSpendEndpoints.GetSpendAsync(
            MonthStart, Now, Now, Context(), new GovernorOptions { DailyBudgetUsd = 5m }, CancellationToken.None);

        AiSpendResponse body = Body(result);
        body.TotalUsd.Should().Be(1.30m);
        body.DailyBudgetUsd.Should().Be(5m);
        body.ByModel.Should().BeEquivalentTo(
            new[] { new AiSpendModelSlice("claude-opus-5", 1.00m), new AiSpendModelSlice("claude-haiku-4-5", 0.30m) },
            options => options.WithStrictOrdering(), "ordered by cost, highest first");
        body.ByDay.Should().BeEquivalentTo(
            new[] { new AiSpendDaySlice(new DateOnly(2026, 7, 14), 0.10m), new AiSpendDaySlice(new DateOnly(2026, 7, 15), 1.20m) },
            options => options.WithStrictOrdering(), "grouped by Central trading day, earliest first");
    }

    [Fact]
    public async Task GetSpendAsync_ShouldSumTodayOnTheCentralDayWindow_NotTheUtcDate()
    {
        // 04:00Z on the 15th is 23:00 CDT on the 14th -- still the 14th's Central day, so NOT today.
        await SeedAsync(_operator, "m", 0.50m, new DateTimeOffset(2026, 7, 15, 4, 0, 0, TimeSpan.Zero));
        // 06:00Z on the 15th is 01:00 CDT on the 15th -- today.
        await SeedAsync(_operator, "m", 0.25m, new DateTimeOffset(2026, 7, 15, 6, 0, 0, TimeSpan.Zero));

        IResult result = await AiSpendEndpoints.GetSpendAsync(
            null, null, Now, Context(), new GovernorOptions { DailyBudgetUsd = 5m }, CancellationToken.None);

        Body(result).TodayUsd.Should().Be(0.25m, "only the row on the 15th's Central day counts toward today");
    }

    [Fact]
    public async Task GetSpendAsync_ShouldScopeToTheOwner()
    {
        await SeedAsync(_operator, "mine", 0.30m, Now);
        await SeedAsync(Guid.NewGuid(), "theirs", 9.99m, Now); // another operator's row (R-20)

        IResult result = await AiSpendEndpoints.GetSpendAsync(
            MonthStart, Now, Now, Context(), new GovernorOptions(), CancellationToken.None);

        AiSpendResponse body = Body(result);
        body.TotalUsd.Should().Be(0.30m, "the R-20 filter scopes the read to the operator's own spend (ADR-0015)");
        body.ByModel.Should().ContainSingle().Which.Model.Should().Be("mine");
    }

    [Fact]
    public async Task GetSpendAsync_ShouldReportNullBudgetAndZeros_WhenInertAndEmpty()
    {
        IResult result = await AiSpendEndpoints.GetSpendAsync(
            null, null, Now, Context(), new GovernorOptions(), CancellationToken.None); // DailyBudgetUsd null

        AiSpendResponse body = Body(result);
        body.TotalUsd.Should().Be(0m);
        body.TodayUsd.Should().Be(0m);
        body.DailyBudgetUsd.Should().BeNull("an inert governor has no cap to report");
        body.ByModel.Should().BeEmpty();
        body.ByDay.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSpendAsync_ShouldRejectAnInvertedPeriod()
    {
        IResult result = await AiSpendEndpoints.GetSpendAsync(
            Now, MonthStart, Now, Context(), new GovernorOptions(), CancellationToken.None); // from after to

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
