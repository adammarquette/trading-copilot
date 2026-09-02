using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for <see cref="OutcomeJournalService.ComposeClosedTradeOutcomesAsync"/>
/// (gh#940, of gh#909; R-9) — written from gh#940's own text and the R-9 requirement, not from the writer. Real
/// Postgres is what witnesses the mechanism this writer rests on: <c>IX_Outcomes_TradeId</c> is what makes a second
/// outcome for one trade impossible — EF-InMemory has no unique indexes at all.
/// </summary>
/// <remarks>
/// Every case drives the writer's own public method, over <c>Trade</c> rows seeded straight through the
/// <see cref="TradingCopilotDbContext"/> — the writer's real input contract, not a production-computed answer
/// handed to it. Uses <see cref="OutcomeTestPostgresFactory"/>, which strips every hosted service (including the
/// writer's own five-minute-interval <c>OutcomeJournalHost</c>), so the only thing ever composing outcomes in
/// these tests is the call each case makes. The idempotency case is deliberately <b>not</b> a <c>Task.WhenAll</c>
/// race on the writer's own method — that was tried and this suite's own prove-red pass caught it stayed green
/// with <c>IX_Outcomes_TradeId</c> dropped entirely, because a container-local round trip can resolve the pair
/// sequentially through the anti-join alone, with or without the index behind it. It instead forces the exact
/// time-of-check-time-of-use gap deterministically (see the case's own remarks).
/// </remarks>
public class OutcomeWriterIntegrationTests : IClassFixture<OutcomeTestPostgresFactory>
{
    private readonly OutcomeTestPostgresFactory _factory;

    public OutcomeWriterIntegrationTests(OutcomeTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // The idempotency race — the reason the writer exists.
    // =============================================================================================================

    [Fact]
    public async Task ComposeClosedTradeOutcomes_ShouldRejectASecondOutcomeForTheSameTrade_ViaTheUniqueIndex()
    {
        // Two composes of the same closed trade must never mint two Outcome rows. A Task.WhenAll race on the
        // writer's own method is NOT a reliable way to witness this: the anti-join alone resolves the ordinary
        // case, and a container-local round trip can complete a full compose-and-commit before a "concurrent"
        // second call ever reaches its own anti-join query — so the pair can pass whether or not the unique index
        // is even there. Proven directly: a WhenAll-raced version of this case stayed green with
        // IX_Outcomes_TradeId dropped entirely (this suite's own prove-red pass). This case instead forces the
        // exact time-of-check-time-of-use gap the index exists to close: the first compose runs and commits
        // normally, then a second writer attempts the identical row shape a racing pass would have built —
        // deterministically proving the unique filtered index, not scheduling luck, is what makes a second
        // outcome for one trade impossible.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 120m);

        int firstPass = await ComposeAsync();
        firstPass.Should().Be(1, "the first pass composes the trade's outcome normally");

        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(new Outcome
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeId,
                Resolution = OutcomeResolution.Win,
                Simulated = false,
            });

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>(
                "a second outcome for the same trade must be impossible — exactly the shape a racing pass's "
                + "insert would take")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_Outcomes_TradeId"
                        || error.MessageText.Contains("IX_Outcomes_TradeId", StringComparison.Ordinal)));
        });

        List<Outcome> outcomes = await OutcomesForAccountAsync(accountId);
        outcomes.Should().ContainSingle(
            "exactly one outcome survives — the rejected second insert never lands, so a double-write can never "
            + "double-count the day's realized result");
    }

    // =============================================================================================================
    // Refuse-don't-guess.
    // =============================================================================================================

    [Fact]
    public async Task ComposeClosedTradeOutcomes_ShouldComposeNothing_ForAClosedTradeWithNoRealizedResult()
    {
        // A closed trade that carries no signed RealizedPnL is unresolvable — the writer must not guess a sign.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        await SeedClosedTradeAsync(owner, accountId, realizedPnL: null);

        int written = await ComposeAsync();

        written.Should().Be(0, "a closed trade with no realized result composes no outcome");
        (await OutcomesForAccountAsync(accountId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomes_ShouldComposeNothing_ForAnOpenTrade()
    {
        // An open trade (ClosedAt null) is not a terminal fact yet — nothing to resolve.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        await SeedOpenTradeAsync(owner, accountId);

        int written = await ComposeAsync();

        written.Should().Be(0, "an open trade composes no outcome");
        (await OutcomesForAccountAsync(accountId)).Should().BeEmpty();
    }

    // =============================================================================================================
    // Sign mapping over real numeric.
    // =============================================================================================================

    [Theory]
    [InlineData(150.25, OutcomeResolution.Win)]
    [InlineData(-75.50, OutcomeResolution.Loss)]
    [InlineData(0, OutcomeResolution.NoFillScratch)]
    public async Task ComposeClosedTradeOutcomes_ShouldMapTheRealizedSign_OverRealNumeric(
        decimal realizedPnL, OutcomeResolution expected)
    {
        // Positive / negative / exactly-zero, read back through Postgres's real `numeric` column — not a decimal
        // held only in .NET memory, so a truncation or provider-mapping defect on the sign itself would show here.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        await SeedClosedTradeAsync(owner, accountId, realizedPnL);

        int written = await ComposeAsync();

        written.Should().Be(1);
        Outcome outcome = (await OutcomesForAccountAsync(accountId)).Should().ContainSingle().Subject;
        outcome.Resolution.Should().Be(expected);
    }

    // =============================================================================================================
    // Cross-owner sweep — a background host with no ambient request user.
    // =============================================================================================================

    [Fact]
    public async Task ComposeClosedTradeOutcomes_ShouldStampEachOutcomeWithItsOwnTradesOwner_AcrossOperators()
    {
        // The sweep runs from a background host, so it reads across owners with IgnoreQueryFilters and must stamp
        // each written outcome from its OWN trade — not from whichever owner happened to be seeded first, and
        // never from an ambient caller (there is none).
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        Guid accountA = await SeedAccountAsync(ownerA);
        Guid accountB = await SeedAccountAsync(ownerB);
        await SeedClosedTradeAsync(ownerA, accountA, realizedPnL: 10m);
        await SeedClosedTradeAsync(ownerB, accountB, realizedPnL: -10m);

        int written = await ComposeAsync();

        written.Should().Be(2);
        Outcome outcomeA = (await OutcomesForAccountAsync(accountA)).Should().ContainSingle().Subject;
        Outcome outcomeB = (await OutcomesForAccountAsync(accountB)).Should().ContainSingle().Subject;
        outcomeA.UserId.Should().Be(ownerA, "the outcome is stamped from ITS OWN trade's owner, not a shared caller");
        outcomeB.UserId.Should().Be(ownerB);
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private async Task<int> ComposeAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
        return await service.ComposeClosedTradeOutcomesAsync(CancellationToken.None);
    }

    private async Task<List<Outcome>> OutcomesForAccountAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Outcomes
            .IgnoreQueryFilters()
            .Where(outcome => outcome.TradeId != null)
            .Join(
                database.Trades.IgnoreQueryFilters().Where(trade => trade.AccountId == accountId),
                outcome => outcome.TradeId,
                trade => trade.Id,
                (outcome, _) => outcome)
            .ToListAsync();
    }

    private async Task<Guid> SeedAccountAsync(Guid owner)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = $"key-{Guid.NewGuid():N}"[..16],
            });
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = $"ACC-{Guid.NewGuid():N}"[..16],
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            await db.SaveChangesAsync();
        });

        return accountId;
    }

    private async Task<Guid> SeedClosedTradeAsync(Guid owner, Guid accountId, decimal? realizedPnL)
    {
        Guid tradeId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(new Trade
            {
                Id = tradeId,
                UserId = owner,
                AccountId = accountId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                ExitPrice = 5_000m + (realizedPnL ?? 0m),
                RealizedPnL = realizedPnL,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return tradeId;
    }

    private Task SeedOpenTradeAsync(Guid owner, Guid accountId) => ExecuteDbAsync(async db =>
    {
        db.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AccountId = accountId,
            Instrument = "ESM25",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            Mode = TradingMode.Practice,
            ClosedAt = null, // still open
        });
        await db.SaveChangesAsync();
    });

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }
}
