using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Persists the durable pre-transmit intent for the operator's per-position exit and reduce (gh#1161).
/// </summary>
public sealed class PositionActionIntentStore : IPositionActionIntentStore
{
    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<PositionActionIntentStore> _logger;

    /// <summary>Creates the store over the request-scoped database.</summary>
    /// <param name="database">The scoped database (R-20 applies).</param>
    /// <param name="logger">The logger — last resort when a resolve cannot land.</param>
    public PositionActionIntentStore(TradingCopilotDbContext database, ILogger<PositionActionIntentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid> CommitAsync(PositionActionIntentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Action is PositionActionKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), draft.Action, "An intent must name a real action.");
        }

        PositionActionIntent row = new()
        {
            Id = Guid.NewGuid(),
            UserId = draft.OwnerUserId,
            AccountId = draft.AccountId,
            Action = (int)draft.Action,
            VenueAccountKey = draft.VenueAccountKey,
            Instrument = draft.Instrument,
            Contract = draft.Contract,
            RequestedQuantity = draft.RequestedQuantity,
            Status = PositionActionIntentStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _database.PositionActionIntents.Add(row);
        await _database.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    /// <inheritdoc />
    public async Task ResolveSafelyAsync(Guid intentId, string outcome, DateTimeOffset resolvedAt)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

            PositionActionIntent? row = await _database.PositionActionIntents
                .FirstOrDefaultAsync(candidate => candidate.Id == intentId, CancellationToken.None);
            if (row is null || row.Status is not PositionActionIntentStatus.Open)
            {
                return;
            }

            row.Status = PositionActionIntentStatus.Resolved;
            row.Outcome = outcome;
            row.ResolvedAt = resolvedAt;
            await _database.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Could not resolve position-action intent {IntentId} (outcome {Outcome}); the action itself is "
                + "unaffected and the Open row remains for the reconcile sweep (gh#1161).",
                intentId,
                outcome);
        }
    }
}
