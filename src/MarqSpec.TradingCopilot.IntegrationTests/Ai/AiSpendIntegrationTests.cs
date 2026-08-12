using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.Api;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Pre-merge integration coverage for the <b>AI-spend ledger + governor</b> (gh#479 ⇒ gh#448 / gh#431 / gh#478,
/// R-20, ADR-0008) against <b>real Postgres</b>. The unit tier proves the governor's arithmetic and the ledger's
/// fail-open contract in isolation; what only a real database can witness is the part gh#479 is about — a
/// <c>SUM</c> over an empty window returning SQL <c>NULL</c>, the seven <c>CK_AiUsage_*</c> checks, the
/// <c>numeric(18,8)</c> round-trip, and the Central-trading-day <c>timestamptz</c> window boundary.
/// </summary>
/// <remarks>
/// The seam doubled is the outbound <see cref="AdversarialLlmProvider"/> only (the QA-sanctioned model seam); the
/// reviewer, the DI-composed <c>AiUsageLedger</c>, the scan and the notification chain are the shipped composition.
/// The budget is fixed by the factory (<see cref="AiSpendTestPostgresFactory.DailyBudgetUsd"/>) — cases vary the
/// <b>seeded spend</b>, never the config.
/// </remarks>
public class AiSpendIntegrationTests : IClassFixture<AiSpendTestPostgresFactory>
{
    private readonly AiSpendTestPostgresFactory _factory;
    private readonly AgentReviewFixture _fixture;

    public AiSpendIntegrationTests(AiSpendTestPostgresFactory factory)
    {
        _factory = factory;
        // The agent-review route is the governor's live consumer, so its scan/seed/query plumbing (gh#429) is
        // reused verbatim — the suite differs only in the seeded SPEND and the active budget, never the route.
        _fixture = new AgentReviewFixture(factory.Services, factory.CreateClient);
    }

    // =============================================================================================================
    // The AIUsage check constraints — enforcement that exists only against real Postgres (the in-memory provider
    // applies no checks, so the unit tier cannot witness any of these).
    // =============================================================================================================

    /// <summary>Each refusable column, and the check that must name it — a bare throw would not witness THIS check.</summary>
    public static TheoryData<string, string> RefusableRows() => new()
    {
        { "feature-unknown", "CK_AiUsage_Feature_NotUnknown" },
        { "outcome-unknown", "CK_AiUsage_Outcome_NotUnknown" },
        { "tier-unknown", "CK_AiUsage_Tier_NotUnknownOrNull" },
        { "cost-negative", "CK_AiUsage_EstimatedCostUsd_NotNegative" },
        { "input-negative", "CK_AiUsage_InputTokens_NotNegative" },
        { "output-negative", "CK_AiUsage_OutputTokens_NotNegative" },
        { "latency-negative", "CK_AiUsage_LatencyMs_NotNegative" },
    };

    [Theory]
    [MemberData(nameof(RefusableRows))]
    public async Task AiUsage_ShouldRefuseRefusableZerosAndNegatives_ByConstraintName(
        string mutation, string expectedConstraint)
    {
        AiUsageRecord row = ValidRow();
        switch (mutation)
        {
            case "feature-unknown": row.Feature = AiUsageFeature.Unknown; break;
            case "outcome-unknown": row.Outcome = AiUsageOutcome.Unknown; break;
            case "tier-unknown": row.Tier = (LlmModelTier)0; break;
            case "cost-negative": row.EstimatedCostUsd = -0.01m; break;
            case "input-negative": row.InputTokens = -1; break;
            case "output-negative": row.OutputTokens = -1; break;
            case "latency-negative": row.LatencyMs = -1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unmapped mutation");
        }

        Func<Task> save = () => SaveRowAsync(row);

        // Named, not merely "something threw": a bare throw assertion would also pass on an unrelated NOT NULL / PK
        // violation and would not witness THIS constraint. The one mutated column is the only one out of range, so
        // the refusal is deterministic.
        DbUpdateException thrown = (await save.Should().ThrowAsync<DbUpdateException>(
            "a ledger row with a refusable value is rejected by the database")).Which;
        thrown.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(
                expectedConstraint, "the refusal is that named check, not an unrelated violation");
    }

    [Fact]
    public async Task AiUsage_ShouldPersistAWellFormedRow_AsThePositiveControl()
    {
        // The control that keeps the Theory honest: without it, the refusals above could all pass because EVERY
        // insert fails (a broken column mapping, a dead connection) rather than because the checks work. A
        // well-formed row must LAND.
        AiUsageRecord row = ValidRow();

        await SaveRowAsync(row);

        bool persisted = await _factory.WithDatabaseAsync(database =>
            database.AiUsage.IgnoreQueryFilters().AnyAsync(candidate => candidate.Id == row.Id));
        persisted.Should().BeTrue("a well-formed ledger row is accepted — the checks refuse only the refusable");
    }

    // =============================================================================================================
    // The governor at the window boundary — the gh#479 lead. An empty window must read ZERO so the governor stays
    // ACTIVE and gates the pass; a spend-read that instead FAULTS trips the fail-open catch, nulls the pass, and runs
    // the co-pilot silently un-gated at the start of every trading day. This case guards that invariant end to end,
    // proven-red by injecting a read fault (CallCount → 2).
    //
    // FINDING (gh#479): the issue's literal prove-red — "delete the (decimal?) cast so SUM-over-empty throws" — does
    // NOT reproduce on this stack. EF 10 + Npgsql return 0 for a non-nullable SUM over an empty set, so the cast is
    // defensive, not load-bearing, and ReadWindowSpendAsync's comment that the non-nullable overload "would throw" is
    // outdated. Verified locally: with the cast deleted this case stays GREEN; with the read faulted it goes red.
    // Flagged on the PR for the Coding Agent — QA does not edit production.
    // =============================================================================================================

    [Fact]
    public async Task Scan_ShouldReadZeroAndRunUnblocked_WhenEveryLedgerRowFallsOutsideTheWindow()
    {
        await ResetAsync();
        (Guid userId, Guid accountId) = await _fixture.SetupOperatorAndAccountAsync();

        // TWO armed agent-review triggers on the one crossing: both fire THIS pass, so the governor is evaluated
        // twice against a tally that ACCRUES between them. That second evaluation is the only thing that tells "read
        // 0, governor active" apart from "read threw, governor inert" — at a single fire, spend is 0 either way, so a
        // one-trigger test could not fail on the cast.
        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedTriggerAsync(userId, accountId);
        await _fixture.SeedIndicatorAsync(75m);

        // Every ledger row falls OUTSIDE today's window: a prior trading day's spend at 3× the budget. The window
        // read must EXCLUDE it (return 0), never sum it — and excluding it leaves an empty set, whose SUM is the SQL
        // NULL the (decimal?) cast exists to absorb.
        await SeedUsageAsync(userId, AiSpendTestPostgresFactory.DailyBudgetUsd * 3m, YesterdayUtc);

        // Each fire prices at $6.00 (1M input + 1M output at the pinned triage rates $1 / $5 per million) — over the
        // $5 budget on its own. So the FIRST fire empties the budget and the SECOND must be blocked before it reaches
        // the model — but ONLY if the empty window was read as 0 and left the governor active to accrue.
        _factory.Llm.ReportsUsage(new LlmUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000));
        _factory.Llm.ReturnsSuggestion("long", entry: 5_000m, stop: 4_990m, target: 5_020m);

        int fires = await _fixture.ScanAsync();

        fires.Should().Be(2, "both armed edges fired this pass — a blocked review is still a fire");
        // THE prove-red assertion. Fault ReadWindowSpendAsync (or otherwise fail to read the empty window as 0) and
        // this becomes 2: the governor goes inert and BOTH fires call the model. Correct: the empty window reads 0,
        // the governor accrues the first fire's $6, and the second is blocked short of the model.
        _factory.Llm.CallCount.Should().Be(
            1, "the second fire is blocked by the budget the first exhausted — proving the empty window kept the governor active");
        (await _fixture.SuggestionCountAsync()).Should().Be(1, "only the un-blocked fire stages a proposal");
    }

    // A valid ledger row every check builds from, mutating exactly one column to its refused value. UserId is a bare
    // Guid (the AIUsage table carries no FK to Users), so a check case needs no seeded operator.
    private static AiUsageRecord ValidRow() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Feature = AiUsageFeature.Triage,
        Model = "claude-haiku-4-5",
        Tier = LlmModelTier.Triage,
        Outcome = AiUsageOutcome.Succeeded,
        InputTokens = 1_200,
        OutputTokens = 300,
        EstimatedCostUsd = 0.0027m,
        LatencyMs = 850,
        TraceId = null,
        OccurredAt = new DateTimeOffset(2026, 7, 29, 15, 0, 0, TimeSpan.Zero),
    };

    private Task SaveRowAsync(AiUsageRecord row) => _factory.WithDatabaseAsync(async database =>
    {
        database.AiUsage.Add(row);
        await database.SaveChangesAsync();
        return 0;
    });

    // Well before the Central-day window start for AgentReviewFixture.Now (2026-07-29 15:00Z): a prior trading day,
    // so a row stamped here is unambiguously OUTSIDE today's window. The exact boundary is case 3's subject.
    private static readonly DateTimeOffset YesterdayUtc = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private async Task ResetAsync()
    {
        _factory.Llm.Reset();
        _factory.Pushover.Reset();
        await _factory.ClearOutboxAsync();
        await ClearAiUsageAsync();
        await _fixture.ClearAsync();
    }

    private Task ClearAiUsageAsync() => _factory.WithDatabaseAsync(async database =>
    {
        await database.AiUsage.IgnoreQueryFilters().ExecuteDeleteAsync();
        return 0;
    });

    // Seeds one ledger row of `costUsd` for `userId` at `occurredAt` — the shared-account spend the governor sums
    // across every owner (ADR-0008). Tokens/latency are zero (all `>= 0` checks hold); only the cost + window matter.
    private Task SeedUsageAsync(Guid userId, decimal costUsd, DateTimeOffset occurredAt) =>
        SaveRowAsync(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Feature = AiUsageFeature.Triage,
            Model = "seed",
            Tier = LlmModelTier.Triage,
            Outcome = AiUsageOutcome.Succeeded,
            InputTokens = 0,
            OutputTokens = 0,
            EstimatedCostUsd = costUsd,
            LatencyMs = 0,
            TraceId = null,
            OccurredAt = occurredAt,
        });
}
