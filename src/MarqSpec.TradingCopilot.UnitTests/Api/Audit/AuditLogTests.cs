using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Audit;

/// <summary>
/// The EF-backed audit log (gh#220): it appends records in their own unit of work and no-ops on an empty batch.
/// </summary>
public class AuditLogTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _operator = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private AuditRecord Row() => new()
    {
        UserId = _operator,
        Action = AuditAction.ConnectionLoss,
        Placement = AuditPlacement.Synthetic,
        SyntheticRisk = true,
        Before = "Hidden",
        After = "Orphaned",
        Detail = "a synthetic stop was orphaned",
        RecordedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task WriteAsync_ShouldPersistTheRecords()
    {
        await new AuditLog(Context()).WriteAsync([Row(), Row()], CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.AuditRecords.IgnoreQueryFilters().ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task WriteAsync_ShouldBeNoOp_WhenTheBatchIsEmpty()
    {
        await new AuditLog(Context()).WriteAsync([], CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.AuditRecords.IgnoreQueryFilters().ToListAsync()).Should().BeEmpty();
    }
}
