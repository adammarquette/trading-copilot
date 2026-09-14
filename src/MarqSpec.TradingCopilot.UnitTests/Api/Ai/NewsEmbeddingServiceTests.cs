using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The news-embedding pass (gh#377): populates the pgvector embedding behind each ingested news item. Its
/// <see cref="EmbeddingRecord"/> is relational-only (gh#109) — <c>Vector</c> has no in-memory-provider mapping, so
/// the batch query and the actual embed/upsert are QA integration-tier (#361/#362). What IS unit-testable without
/// a database round-trip of the vector: the degrade (no provider ⇒ no-op), and the AI-spend cap gate (mirrors
/// <see cref="MarqSpec.TradingCopilot.Api.Triggers.TriggerEvaluationServiceTests"/>). The content-hash decision
/// itself is covered directly by <c>EmbeddingContentHashTests</c>.
/// </summary>
public class NewsEmbeddingServiceTests
{
    private static DateTimeOffset Now => new(2026, 7, 29, 15, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IEmbeddingProvider _provider = A.Fake<IEmbeddingProvider>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid? asUser = null) => new(Options, new FixedUser(asUser ?? Guid.NewGuid()));

    private static NewsRecord News(string dedupKey) => new()
    {
        DedupKey = dedupKey,
        Type = "news",
        Url = $"https://site.com/{dedupKey}",
        Title = "FOMC holds rates",
        Summary = "The committee left rates unchanged.",
        PublishedAt = Now,
        Tickers = [],
        SourceFeeds = ["finnhub"],
        RecordedAt = Now,
    };

    // Every test defaults to an INERT governor (a bare GovernorOptions has DailyBudgetUsd null => no cap) unless it
    // passes a configured one -- mirroring TriggerEvaluationServiceTests' own Service() factory.
    private NewsEmbeddingService Service(GovernorOptions? governor = null) => new(
        Context(),
        _provider,
        _ledger,
        new AiSpendGovernor(),
        // Fully qualified: this class's own Options property (the DbContext options) shadows the Microsoft.Extensions
        // .Options.Options static helper, exactly as TriggerEvaluationServiceTests works around the same collision.
        Microsoft.Extensions.Options.Options.Create(governor ?? new GovernorOptions()),
        NullLogger<NewsEmbeddingService>.Instance);

    // Seeds one AIUsage spend row under the given owner -- the governor's per-pass read is PLATFORM-WIDE
    // (IgnoreQueryFilters), so a row seeded under any owner (including the SystemOwner embed sentinel itself)
    // is summed into the gate's floor, exactly as TriggerEvaluationServiceTests' SeedUsageAsync documents.
    private async Task SeedUsageAsync(Guid owner, decimal costUsd, DateTimeOffset occurredAt)
    {
        await using TradingCopilotDbContext context = Context(owner);
        context.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Feature = AiUsageFeature.Embed,
            Model = "embed-english-v3.0",
            Tier = null,
            Outcome = AiUsageOutcome.Succeeded,
            InputTokens = 0,
            OutputTokens = 0,
            EstimatedCostUsd = costUsd,
            LatencyMs = 0,
            OccurredAt = occurredAt,
        });
        await context.SaveChangesAsync();
    }

    // --- Degrade: no embedding provider available ---

    [Fact]
    public async Task EmbedPendingAsync_ShouldNoOp_WhenTheProviderIsUnavailable()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(false);

        int embedded = await Service().EmbedPendingAsync(Now, CancellationToken.None);

        embedded.Should().Be(0);
        // Nothing throws and nothing is attempted -- the structured News rows are untouched (gh#109's degrade
        // posture) and no AIUsage row is written, because no call was ever made.
        A.CallTo(() => _provider.EmbedAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task EmbedPendingAsync_ShouldNotThrow_WhenUnavailable_EvenWithNewsPending()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(false);
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.News.Add(News("a"));
            await seed.SaveChangesAsync();
        }

        Func<Task> act = () => Service().EmbedPendingAsync(Now, CancellationToken.None);

        // The degrade check happens BEFORE the relational-only Embeddings store is ever touched, so the pass is
        // safe to run even against the in-memory provider (gh#109) with real news sitting in the backlog.
        await act.Should().NotThrowAsync();
    }

    // --- The AI-spend cap (gh#448, ADR-0008) mirrors TriggerEvaluationService ---

    [Fact]
    public async Task EmbedPendingAsync_ShouldNoOp_WhenTheDailyBudgetIsAlreadyReached()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        await SeedUsageAsync(SystemOwner.Id, 100m, Now); // 100 spent against a 10 budget -> the governor blocks

        int embedded = await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m })
            .EmbedPendingAsync(Now, CancellationToken.None);

        embedded.Should().Be(0);
        // The pass stops BEFORE it ever reaches the relational-only News/Embeddings query -- proven by the
        // complete absence of any embed call or ledger write, exactly like the trigger scan's BudgetExhausted path.
        A.CallTo(() => _provider.EmbedAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task EmbedPendingAsync_ShouldCountEveryOwnersSpend_WhenCheckingTheSharedCap()
    {
        // R-20 crossed on purpose (ADR-0008: one shared LLM + embeddings account funds every user): an operator's
        // triage spend and a stranger's both count against the SAME platform-wide daily budget the embed pass reads.
        Guid operatorId = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        await SeedUsageAsync(operatorId, 4m, Now);
        await SeedUsageAsync(stranger, 4m, Now);
        await SeedUsageAsync(SystemOwner.Id, 4m, Now); // 12 total against a 10 budget -> blocked

        int embedded = await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m })
            .EmbedPendingAsync(Now, CancellationToken.None);

        embedded.Should().Be(0);
        A.CallTo(() => _provider.EmbedAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task EmbedPendingAsync_ShouldNotBlock_WhenSpendIsUnderTheBudget()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        await SeedUsageAsync(SystemOwner.Id, 2m, Now); // under an 10 budget -> the governor allows

        // Under budget (and available), the pass proceeds PAST both gates into its relational-only work -- which
        // is exactly the part gh#109 makes unreachable on the in-memory provider (EmbeddingRecord is Ignore()d).
        // This is the deliberate unit/integration boundary the card calls out: reaching that wall (rather than a
        // silent, ungated 0) is itself proof neither gate short-circuited the pass for the wrong reason. The real
        // embed/upsert round-trip is QA integration-tier (#361/#362).
        Func<Task> act = () => Service(governor: new GovernorOptions { DailyBudgetUsd = 10m })
            .EmbedPendingAsync(Now, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "EmbeddingRecord is unmapped on the in-memory provider (gh#109); reaching that query proves the cap did not block");
    }

    [Fact]
    public async Task EmbedPendingAsync_ShouldReadNoSpend_AndRunUnGated_WhenNoBudgetIsConfigured()
    {
        // The default, no-cap status quo (gh#448): with DailyBudgetUsd absent, the governor is INERT, so the pass
        // never even reads AiUsage before proceeding -- proven the same way, by reaching the relational-only wall.
        A.CallTo(() => _provider.IsAvailable).Returns(true);

        Func<Task> act = () => Service().EmbedPendingAsync(Now, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
