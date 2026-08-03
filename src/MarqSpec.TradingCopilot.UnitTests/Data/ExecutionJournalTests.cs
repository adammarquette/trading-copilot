using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The execution/journal spine (gh#7): Suggestion → Order → Fill, and the journaled Trade. The InMemory tests
/// cover shape, ownership, and linkage; the R-14 constraints (mode CHECKs and the order-mode-matches-account
/// trigger) are database objects, proven against live Postgres.
/// </summary>
public class ExecutionJournalTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(asUser ?? _operator));
    }

    private async Task<Guid> SeedAccountAsync()
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K-1234",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task JournalEntities_ShouldRoundTrip_OwnedAndLinked()
    {
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        await using (TradingCopilotDbContext context = Context())
        {
            context.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = _operator,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Side = OrderSide.Buy,
                Size = 2,
                EntryPrice = 5301.25m,
                StopPrice = 5296.25m,
                TargetPrice = 5311.25m,
                Mode = TradingMode.Practice,
                State = SuggestionState.Active,
                CreatedAt = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero),

                // Required since gh#542/#543/#544. This suite round-trips the journal spine and its links, which
                // these fields do not affect; ExpiresAt sits after CreatedAt so the CHECK is satisfied.
                Rationale = "seeded",
                CitedIndicator = "rsi",
                CitedPeriod = 14,
                CitedResolutionMinutes = 1,
                Confidence = 60,
                ExpiresAt = new DateTimeOffset(2026, 7, 22, 15, 30, 0, TimeSpan.Zero),
            });
            context.Orders.Add(new Order
            {
                Id = orderId,
                UserId = _operator,
                AccountId = accountId,
                SuggestionId = suggestionId,
                Instrument = "CON.F.US.MES.U26",
                Side = OrderSide.Buy,
                Size = 2,
                Type = OrderType.Limit,
                LimitPrice = 5301.25m,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                VenueOrderKey = "889001",
                PlacedAt = new DateTimeOffset(2026, 7, 22, 14, 30, 5, TimeSpan.Zero),
            });
            context.Fills.Add(new Fill
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                OrderId = orderId,
                VenueFillKey = "F-1",
                Price = 5301.25m,
                Size = 2,
                Fees = 2.48m,
                ExecutedAt = new DateTimeOffset(2026, 7, 22, 14, 30, 9, TimeSpan.Zero),
            });
            context.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                AccountId = accountId,
                SuggestionId = suggestionId,
                Instrument = "CON.F.US.MES.U26",
                Side = OrderSide.Buy,
                Size = 2,
                EntryPrice = 5301.25m,
                ExitPrice = 5311.25m,
                RealizedPnL = 100m,
                Mode = TradingMode.Practice,
                ClosedAt = new DateTimeOffset(2026, 7, 22, 15, 5, 0, TimeSpan.Zero),
            });
            await context.SaveChangesAsync();
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.SingleAsync()).State.Should().Be(SuggestionState.Active);
        Order order = await reload.Orders.SingleAsync();
        order.SuggestionId.Should().Be(suggestionId);
        order.Status.Should().Be(OrderStatus.Working);
        (await reload.Fills.SingleAsync()).OrderId.Should().Be(orderId);
        Trade trade = await reload.Trades.SingleAsync();
        trade.RealizedPnL.Should().Be(100m);
        trade.Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task JournalEntities_ShouldBeInvisible_ToAnotherOperator()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext context = Context())
        {
            context.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Side = OrderSide.Sell,
                Size = 1,
                Type = OrderType.Market,
                Status = OrderStatus.Filled,
                Mode = TradingMode.Practice,
                PlacedAt = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero),
            });
            await context.SaveChangesAsync();
        }

        // Default-deny (R-20): the journal is the most sensitive surface in the workspace.
        await using TradingCopilotDbContext otherOperator = Context(asUser: Guid.NewGuid());
        (await otherOperator.Orders.AnyAsync()).Should().BeFalse();
    }
}
