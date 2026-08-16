using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>read_positions</c> chat tool (gh#929) — reports the operator's currently open positions from
/// <b>venue truth</b> (through <see cref="IPositionReconciler"/>, gh#193) across their active accounts, each tagged
/// with its <see cref="PositionMarkBasis"/> so a settlement re-mark is never read as live movement and an
/// unreachable venue reads as declared-unknown (R-13, R-19, ADR-0013). <b>Read-only</b>: it injects only the
/// reconcile <i>read</i> seam and the owner-scoped <see cref="TradingCopilotDbContext"/> (R-20), reaching no exit /
/// order / write path — the model can learn what the operator holds but can never change it (enforcement below the
/// model).
/// </summary>
public sealed class ReadPositionsTool : IChatTool
{
    private readonly TradingCopilotDbContext _database;
    private readonly IPositionReconciler _reconciler;
    private readonly ILogger<ReadPositionsTool> _logger;

    /// <summary>Creates the tool.</summary>
    /// <param name="database">The scoped, owner-filtered database (R-20) the operator's accounts are read from.</param>
    /// <param name="reconciler">The read-only venue-truth position reconcile seam.</param>
    /// <param name="logger">The logger (a read fault is logged, then failed closed).</param>
    public ReadPositionsTool(
        TradingCopilotDbContext database,
        IPositionReconciler reconciler,
        ILogger<ReadPositionsTool> logger)
    {
        _database = database;
        _reconciler = reconciler;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "read_positions";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Read the operator's currently open positions from venue truth across their trading accounts — the "
        + "contract, signed net quantity (positive long, negative short), average entry price, and each account's "
        + "mark basis (Live, Settlement re-mark, or Unknown when the venue could not be reached). Use when the trader "
        + "asks what they are holding, their open risk, or whether they are flat. Read-only: it never places, sizes, "
        + "exits, or changes a position.",
        "{\"type\":\"object\",\"properties\":{}}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        try
        {
            // The operator's own ACTIVE accounts (the R-20 tenant filter scopes this to the caller). A deactivated
            // login yields no venue truth, so reconciling it would only ever return Unknown -- skip it. AsNoTracking:
            // a read-only tool never tracks on the write-capable request context.
            var accounts = await _database.Accounts
                .AsNoTracking()
                .Where(account => account.IsActive)
                .OrderBy(account => account.Name)
                .Select(account => new { account.Id, account.Name, account.Mode })
                .ToListAsync(cancellationToken);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var reported = new List<object>();
            foreach (var account in accounts)
            {
                PositionReconciliation? reconciliation =
                    await _reconciler.ReconcileAsync(account.Id, now, cancellationToken);
                if (reconciliation is null)
                {
                    continue; // account not found / no longer owned between the enumerate and the reconcile -- skip
                }

                reported.Add(new
                {
                    account = account.Name,
                    mode = account.Mode.ToString(),
                    markBasis = reconciliation.Basis.ToString(),
                    // Only OPEN positions -- a flat contract (net 0) is not a holding. An Unknown reconcile carries no
                    // positions by contract, so its empty list plus the "Unknown" basis reads as declared-unknown,
                    // never a fabricated "flat" (ADR-0013).
                    positions = reconciliation.Positions
                        .Where(position => !position.IsFlat)
                        .Select(position => new
                        {
                            contract = position.Contract.Key,
                            netQuantity = position.NetQuantity,
                            side = position.IsLong ? "Long" : "Short",
                            averagePrice = position.AveragePrice.Value,
                        }),
                });
            }

            return JsonSerializer.Serialize(new { accounts = reported });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a read fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "read_positions read faulted; returning a fail-closed tool error.");
            return Error("Your positions could not be read right now.");
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
