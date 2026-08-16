using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>get_quote</c> chat tool (gh#925) — reads the most recent stored OHLCV bar (a recent "quote") for an
/// instrument from the clean-historical bar store (<c>BarRecord</c>, gh#302 / gh#644). <b>Read-only</b>: a single read
/// over the bar store, reaching no order / write path. Market data is <b>global / shared</b> (R-20 draws bars outside
/// owner-scoping), so unlike the journal read this carries no tenant filter — it reads the market, not the operator's
/// private data.
/// </summary>
public sealed class GetQuoteTool : IChatTool
{
    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<GetQuoteTool> _logger;

    /// <summary>Creates the tool over the database.</summary>
    /// <param name="database">The database (bars are global, not owner-filtered).</param>
    /// <param name="logger">The logger (a read fault is logged, then failed closed).</param>
    public GetQuoteTool(TradingCopilotDbContext database, ILogger<GetQuoteTool> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "get_quote";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Read the most recent stored OHLCV bar (a recent quote) for a futures instrument — open / high / low / close, "
        + "volume, the venue, the bar size, and when the bar opened. Use when the trader asks about the current or "
        + "recent price of a symbol (e.g. ES, NQ). Read-only market data — it never places or changes an order.",
        "{\"type\":\"object\",\"properties\":{\"instrument\":{\"type\":\"string\","
        + "\"description\":\"The instrument symbol, e.g. ES or NQ.\"},\"resolution\":{\"type\":\"integer\","
        + "\"description\":\"Optional bar size in minutes; omit for the most recent bar at any size.\"}},"
        + "\"required\":[\"instrument\"]}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        string? instrument;
        int? resolution;
        try
        {
            (instrument, resolution) = ParseInput(inputJson);
        }
        catch (JsonException)
        {
            return Error("The tool input was not valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(instrument))
        {
            return Error("An 'instrument' symbol is required.");
        }

        // Normalise exactly as the market-data writers key their rows (InstrumentId.ToString()), or a raw symbol would
        // never match the stored key (gh#644).
        if (!InstrumentId.TryParse(instrument, out InstrumentId parsed))
        {
            return Error($"'{instrument}' is not a valid instrument symbol.");
        }

        string key = parsed.ToString();

        try
        {
            BarRecord? bar = await _database.Bars
                .AsNoTracking()
                .Where(record => record.Instrument == key && (resolution == null || record.ResolutionMinutes == resolution))
                .OrderByDescending(record => record.BucketStart)
                .FirstOrDefaultAsync(cancellationToken);

            if (bar is null)
            {
                return Error($"No market data for instrument '{key}'.");
            }

            return JsonSerializer.Serialize(new
            {
                quote = new
                {
                    instrument = bar.Instrument,
                    venue = bar.Venue,
                    resolutionMinutes = bar.ResolutionMinutes,
                    bucketStart = bar.BucketStart,
                    open = bar.Open,
                    high = bar.High,
                    low = bar.Low,
                    close = bar.Close,
                    volume = bar.Volume,
                },
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a read fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "get_quote read faulted; returning a fail-closed tool error.");
            return Error("The quote could not be read right now.");
        }
    }

    private static (string? Instrument, int? Resolution) ParseInput(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return (null, null);
        }

        using JsonDocument document = JsonDocument.Parse(inputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? instrument = document.RootElement.TryGetProperty("instrument", out JsonElement instrumentElement)
            && instrumentElement.ValueKind == JsonValueKind.String
                ? instrumentElement.GetString()
                : null;

        int? resolution = document.RootElement.TryGetProperty("resolution", out JsonElement resolutionElement)
            && resolutionElement.ValueKind == JsonValueKind.Number
            && resolutionElement.TryGetInt32(out int parsedResolution)
                ? parsedResolution
                : null;

        return (instrument, resolution);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
