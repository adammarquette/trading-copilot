using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>query_journal</c> chat tool (gh#925) — reads the trader's most recent <b>closed</b> trades from their journal
/// (<c>Trade</c>, gh#731; its <c>Outcome</c> is gh#832). <b>Read-only</b>: it opens the scoped
/// <see cref="TradingCopilotDbContext"/> (R-20 owner-filtered by the tenant query filter) and issues a single read; it
/// reaches no order / write path, so the model can learn what the trader did but can never change it.
/// </summary>
public sealed class QueryJournalTool : IChatTool
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<QueryJournalTool> _logger;

    /// <summary>Creates the tool over the scoped database.</summary>
    /// <param name="database">The scoped, owner-filtered database.</param>
    /// <param name="logger">The logger (a read fault is logged, then failed closed).</param>
    public QueryJournalTool(TradingCopilotDbContext database, ILogger<QueryJournalTool> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "query_journal";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Read the trader's most recent CLOSED trades from their journal — instrument, side, size, entry/exit price, "
        + "realized P&L, mode (practice/live), and when it closed. Use when the trader asks about their recent trading, "
        + "their results, or a past trade. Read-only: it never places, sizes, or changes an order.",
        "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\","
        + "\"description\":\"How many recent trades to return (default 10, max 50).\"}}}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        int limit;
        try
        {
            limit = ParseLimit(inputJson);
        }
        catch (JsonException)
        {
            return Error("The tool input was not valid JSON.");
        }

        try
        {
            // Materialize the closed trades, then project in memory -- the enum ToString() renders client-side rather
            // than pushing an untranslatable expression into SQL. The tenant query filter scopes this to the owner (R-20).
            List<Trade> rows = await _database.Trades
                .AsNoTracking() // a read-only tool never tracks -- the endpoint SaveChanges (UpdatedAt bump) must not see these
                .Where(trade => trade.ClosedAt != null)
                .OrderByDescending(trade => trade.ClosedAt)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var trades = rows.Select(trade => new
            {
                instrument = trade.Instrument,
                side = trade.Side.ToString(),
                size = trade.Size,
                entryPrice = trade.EntryPrice,
                exitPrice = trade.ExitPrice,
                realizedPnL = trade.RealizedPnL,
                mode = trade.Mode.ToString(),
                closedAt = trade.ClosedAt,
            });

            return JsonSerializer.Serialize(new { trades });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a read fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "query_journal read faulted; returning a fail-closed tool error.");
            return Error("The journal could not be read right now.");
        }
    }

    private static int ParseLimit(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return DefaultLimit;
        }

        using JsonDocument document = JsonDocument.Parse(inputJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("limit", out JsonElement limitElement)
            && limitElement.ValueKind == JsonValueKind.Number
            && limitElement.TryGetInt32(out int limit)
                ? Math.Clamp(limit, 1, MaxLimit)
                : DefaultLimit;
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
