using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>Validates <see cref="DiscordOptions"/> at startup (gh#1157). Never echoes a value.</summary>
public sealed class DiscordOptionsValidator : IValidateOptions<DiscordOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DiscordOptions options) =>
        throw new NotImplementedException();
}
