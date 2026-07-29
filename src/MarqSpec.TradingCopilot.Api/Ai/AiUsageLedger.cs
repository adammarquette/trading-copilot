using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The persisted AIUsage ledger (gh#431, ADR-0008 / ADR-0002): writes one <see cref="AiUsageRecord"/> per AI call,
/// under the entry owner's R-20 scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-open by contract.</b> A ledger write must never fail or roll back the AI call it records. It writes in its
/// <b>own</b> short-lived per-owner context — deliberately <b>not</b> enrolled in the caller's transaction — so a
/// CHECK violation or a DB blip here can never undo a committed suggestion / firing. Any fault is logged
/// (spend-blindness is a real fault, never silent) and swallowed; only the caller's own cancellation propagates.
/// </para>
/// <para>
/// Stateless, so a singleton — it holds only the shared context options and constructs a fresh owner-scoped context
/// per call, exactly as the trigger scan does. The owner is the entry's, so the R-20 filter guarantees the row is
/// written under the scope it belongs to.
/// </para>
/// </remarks>
public class AiUsageLedger : IAiUsageLedger
{
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ILogger<AiUsageLedger> _logger;

    /// <summary>Creates the ledger.</summary>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context per write.</param>
    /// <param name="logger">The logger. A failed write is logged as an error, never silently swallowed.</param>
    public AiUsageLedger(DbContextOptions<TradingCopilotDbContext> options, ILogger<AiUsageLedger> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AiUsageEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await WriteAsync(entry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // our own shutdown, not a bookkeeping fault to swallow
        }
        catch (Exception error)
        {
            // FAIL-OPEN: the AI result is already delivered; a ledger fault must never surface to the caller. Log it
            // (spend-blindness is a real fault) and move on -- the ledger is a floor, not a guarantee.
            _logger.LogError(
                error,
                "AIUsage ledger write failed for owner {Owner} ({Feature}/{Outcome}); the AI result is unaffected.",
                entry.UserId,
                entry.Cost.Feature,
                entry.Cost.Outcome);
        }
    }

    /// <summary>The actual write, in its own owner-scoped context. Virtual so a test can force a failure without a real DB fault.</summary>
    /// <param name="entry">The entry to persist.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    protected virtual async Task WriteAsync(AiUsageEntry entry, CancellationToken cancellationToken)
    {
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(entry.UserId));

        database.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = entry.UserId,
            Feature = entry.Cost.Feature,
            Model = entry.Cost.Model,
            Tier = entry.Cost.Tier,
            Outcome = entry.Cost.Outcome,
            InputTokens = entry.Cost.InputTokens,
            OutputTokens = entry.Cost.OutputTokens,
            EstimatedCostUsd = entry.Cost.EstimatedCostUsd,
            LatencyMs = (long)entry.Cost.Latency.TotalMilliseconds,
            TraceId = entry.TraceId,
            OccurredAt = entry.OccurredAt,
        });

        await database.SaveChangesAsync(cancellationToken);
    }
}
