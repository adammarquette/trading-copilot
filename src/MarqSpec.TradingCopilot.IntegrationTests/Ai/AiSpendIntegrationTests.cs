using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
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

    public AiSpendIntegrationTests(AiSpendTestPostgresFactory factory)
    {
        _factory = factory;
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
}
