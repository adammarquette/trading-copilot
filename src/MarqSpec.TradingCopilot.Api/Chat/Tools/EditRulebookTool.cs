using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>STUB — red-proof scaffold for gh#1135. Replaced by the implementation in the next commit.</summary>
public sealed class EditRulebookTool : IChatTool
{
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ICurrentUser _currentUser;
    private readonly IChatTurnScope _turnScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<EditRulebookTool> _logger;

    /// <summary>Creates the tool.</summary>
    /// <param name="options">The shared context options.</param>
    /// <param name="currentUser">The request's operator (R-20).</param>
    /// <param name="turnScope">The conversation this turn runs in.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public EditRulebookTool(
        DbContextOptions<TradingCopilotDbContext> options,
        ICurrentUser currentUser,
        IChatTurnScope turnScope,
        TimeProvider clock,
        ILogger<EditRulebookTool> logger)
    {
        _options = options;
        _currentUser = currentUser;
        _turnScope = turnScope;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "edit_rulebook";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(Name, "stub", "{\"type\":\"object\",\"properties\":{}}");

    /// <inheritdoc />
    public Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        _ = _options;
        _ = _currentUser;
        _ = _turnScope;
        _ = _clock;
        _ = _logger;
        _ = inputJson;
        _ = cancellationToken;
        return Task.FromResult(JsonSerializer.Serialize(new { error = "not implemented" }));
    }
}
