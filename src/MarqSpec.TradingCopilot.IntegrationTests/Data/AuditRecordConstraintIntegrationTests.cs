using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// Pre-merge integration coverage for the <b>AuditRecord DB check constraints and the R-20 owner filter</b>
/// (gh#789 ⇒ gh#765; engineering §9, ADR-0007) against <b>real Postgres</b>. The unit tier proves the kill-switch
/// and auto-flatten write sites stamp the right fields against a fake <c>IAuditLog</c> — but the fake is in-memory,
/// so it applies <b>no</b> check constraint and enforces <b>no</b> query filter. The two things only a real database
/// can witness are exactly what gh#765 added below the model and what this suite exists to prove:
/// <list type="bullet">
///   <item><c>CK_AuditRecords_Source_NotUnknown</c> — a persisted <see cref="AuditSource"/> is never the refusable
///     zero, while a genuinely absent source (the stop-plan actions) stays null.</item>
///   <item><c>CK_AuditRecords_Source_MatchesAction</c> — the correlation enforced <i>below the model</i>: a safety
///     action (kill-switch engage/disengage = 5/6, auto-flatten = 7) MUST carry a source, and a stop-plan action
///     (1–4) MUST leave it null. The exact "record a safety action with an unknowable trigger" defeat the in-memory
///     unit tier cannot catch.</item>
///   <item>the <b>R-20</b> per-operator query filter over <c>AuditRecords</c> — a row is visible only to its owner.</item>
/// </list>
/// </summary>
/// <remarks>
/// Nothing is doubled: writes go through the real <see cref="TradingCopilotDbContext"/> against the shipped model
/// (the container is migrated to head on host boot). Each refusal row is out of range in <b>exactly one</b> column,
/// so the constraint named in the failure is deterministic — a bare "something threw" would also pass on an
/// unrelated violation and witness nothing. The positive controls keep the theory honest: a well-formed row of each
/// shape must LAND, or the refusals could all be passing because every insert fails.
/// </remarks>
public class AuditRecordConstraintIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;

    private static readonly Guid _defaultOwner = Guid.Parse("0789aaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 12, 14, 0, 0, TimeSpan.Zero);

    public AuditRecordConstraintIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Each refusable row, and the single check that must name it. A bare throw would not witness THIS check.</summary>
    public static TheoryData<string, string> RefusableRows() => new()
    {
        { "action-unknown", "CK_AuditRecords_Action_NotUnknown" },
        { "placement-unknown", "CK_AuditRecords_Placement_NotUnknown" },
        { "source-unknown-on-safety", "CK_AuditRecords_Source_NotUnknown" },
        { "safety-action-missing-source", "CK_AuditRecords_Source_MatchesAction" },
        { "stop-plan-action-with-source", "CK_AuditRecords_Source_MatchesAction" },
    };

    [Theory]
    [MemberData(nameof(RefusableRows))]
    public async Task AuditRecord_ShouldRefuseTheRefusableRow_ByConstraintName(string mutation, string expectedConstraint)
    {
        // Each row is out of range in exactly one dimension; every other column is valid, so the refusal is the
        // named check and nothing else. AuditSource is cast from 0 (not the named Unknown) because C# would let an
        // out-of-enum int through just the same — the DB is the backstop.
        AuditRecord row = mutation switch
        {
            "action-unknown" => Row(AuditAction.Unknown, AuditPlacement.None, source: null),
            "placement-unknown" => Row(AuditAction.KillSwitchEngaged, AuditPlacement.Unknown, AuditSource.Operator),
            "source-unknown-on-safety" => Row(AuditAction.KillSwitchEngaged, AuditPlacement.None, (AuditSource)0),
            "safety-action-missing-source" => Row(AuditAction.AutoFlatten, AuditPlacement.None, source: null),
            "stop-plan-action-with-source" => Row(AuditAction.OrderCancelled, AuditPlacement.Native, AuditSource.Operator),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unmapped mutation"),
        };

        Func<Task> save = () => SaveAsync(row);

        DbUpdateException thrown = (await save.Should().ThrowAsync<DbUpdateException>(
            "an audit row that is out of range for a check constraint is rejected by the database")).Which;
        thrown.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(
                expectedConstraint, "the refusal is that named check, not an unrelated violation");
    }

    [Fact]
    public async Task AuditRecord_ShouldPersistAWellFormedSafetyRow_AsThePositiveControl()
    {
        // The control that keeps the theory honest: a safety row carrying a source must LAND, or every refusal above
        // could be passing because inserts are broken, not because the checks work.
        AuditRecord row = Row(AuditAction.KillSwitchEngaged, AuditPlacement.None, AuditSource.Operator);

        await SaveAsync(row);

        (await ExistsAsync(row.Id)).Should().BeTrue(
            "a kill-switch row with a source survives every check — the constraints refuse only the refusable");
    }

    [Fact]
    public async Task AuditRecord_ShouldPersistAStopPlanRowWithNullSource_ProvingNullIsAccepted()
    {
        // The other half of CK_AuditRecords_Source_NotUnknown: null is not the refusable zero. A stop-plan lifecycle
        // action concerns no external trigger and carries no source — and that must be accepted, not refused.
        AuditRecord row = Row(AuditAction.ConnectionLoss, AuditPlacement.Synthetic, source: null);

        await SaveAsync(row);

        (await ExistsAsync(row.Id)).Should().BeTrue(
            "a stop-plan action carries no source — a NULL source is accepted; only a persisted Unknown is refused");
    }

    [Fact]
    public async Task AuditRow_ShouldBeVisibleOnlyToItsOwner_UnderTheR20QueryFilter()
    {
        // The safety trail is operator-owned (R-20): a kill/flatten row is stamped with its owner (the operator for a
        // kill, the account's owner for a background flatten) and must be invisible to any other operator — exactly
        // like the connection-loss rows. The fake IAuditLog the unit tier uses enforces no filter; only a real
        // TradingCopilotDbContext bound to a user does.
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        AuditRecord rowA = Row(AuditAction.KillSwitchEngaged, AuditPlacement.None, AuditSource.Operator, owner: ownerA);
        AuditRecord rowB = Row(AuditAction.AutoFlatten, AuditPlacement.None, AuditSource.Scheduler, owner: ownerB);
        await SaveAsync(rowA);
        await SaveAsync(rowB);

        List<Guid> visibleToA = await VisibleToAsync(ownerA);
        visibleToA.Should().Contain(rowA.Id, "an operator sees their own kill-switch audit row")
            .And.NotContain(rowB.Id, "operator A's context must not surface operator B's auto-flatten audit row");

        List<Guid> visibleToB = await VisibleToAsync(ownerB);
        visibleToB.Should().Contain(rowB.Id, "an operator sees their own auto-flatten audit row")
            .And.NotContain(rowA.Id, "operator B's context must not surface operator A's kill-switch audit row");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static AuditRecord Row(
        AuditAction action, AuditPlacement placement, AuditSource? source, Guid? owner = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = owner ?? _defaultOwner,
            Action = action,
            Placement = placement,
            Source = source,
            SyntheticRisk = false,
            Detail = "gh#789 integration audit row",
            RecordedAt = _recordedAt,
        };

    // Writes the row through a context bound to its own owner, so the write is stamped exactly as the production
    // write sites stamp theirs; the query filter never touches an INSERT, so an out-of-range value reaches the check.
    private async Task SaveAsync(AuditRecord row)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext db = new(options, new FixedUser(row.UserId));
        db.AuditRecords.Add(row);
        await db.SaveChangesAsync();
    }

    // The ids an operator's own context surfaces — queried WITHOUT IgnoreQueryFilters, so the R-20 filter applies.
    private async Task<List<Guid>> VisibleToAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext db = new(options, new FixedUser(userId));
        return await db.AuditRecords.Select(record => record.Id).ToListAsync();
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await db.AuditRecords.IgnoreQueryFilters().AnyAsync(record => record.Id == id);
    }

    private sealed class FixedUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}
