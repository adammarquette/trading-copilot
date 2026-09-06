using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>STUB — the failing-test-first placeholder for gh#1134. Replaced by the real tool in the next commit.</summary>
public sealed class GenerateSuggestionTool : IChatTool
{
    /// <summary>Creates the stub.</summary>
    public GenerateSuggestionTool(
        DbContextOptions<TradingCopilotDbContext> options,
        ICurrentUser currentUser,
        ISessionDeadlineSource deadlines,
        ISuggestionRealtimeNotifier notifier,
        TimeProvider clock,
        IOptions<SuggestionOptions> suggestionOptions,
        ILogger<GenerateSuggestionTool> logger)
    {
        _ = options;
        _ = currentUser;
        _ = deadlines;
        _ = notifier;
        _ = clock;
        _ = suggestionOptions;
        _ = logger;
    }

    /// <inheritdoc />
    public string Name => "generate_suggestion";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(Name, "not implemented", "{}");

    /// <inheritdoc />
    public Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken) =>
        Task.FromResult("{\"error\":\"not implemented\"}");
}
