using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Validates <see cref="DiscordOptions"/> at startup (gh#1157), wired with <c>ValidateOnStart</c>.
/// </summary>
/// <remarks>
/// <para>
/// Keyless is a legal configuration and passes. What fails is a <b>mistake</b>: half of the DM pair, a
/// non-snowflake user id, or a webhook that is not an https Discord webhook URL. Each would otherwise boot clean
/// and then fail on the first advisory, invisibly. Restricting the webhook to Discord's own hosts also means a
/// mistyped or hostile value can never turn this channel into a way to post the advisory text to another server.
/// </para>
/// <para>
/// Messages name the <b>key</b>, never the value: a webhook URL carries its token in the path.
/// </para>
/// </remarks>
public sealed class DiscordOptionsValidator : IValidateOptions<DiscordOptions>
{
    private static readonly string[] _webhookHosts =
        ["discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com"];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DiscordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.HasWebhook && !IsDiscordWebhook(options.WebhookUrl!))
        {
            failures.Add(
                "Discord:WebhookUrl must be an https Discord webhook URL "
                + "(https://discord.com/api/webhooks/<id>/<token>).");
        }

        bool hasToken = !string.IsNullOrWhiteSpace(options.BotToken);
        bool hasUser = !string.IsNullOrWhiteSpace(options.OperatorUserId);

        if (hasToken && !hasUser)
        {
            failures.Add("Discord:OperatorUserId is required when Discord:BotToken is set; a DM is only ever sent to a pinned recipient.");
        }

        if (hasUser && !hasToken)
        {
            failures.Add("Discord:BotToken is required when Discord:OperatorUserId is set.");
        }

        if (hasUser && !DiscordNotificationChannel.IsSnowflake(options.OperatorUserId!))
        {
            failures.Add("Discord:OperatorUserId must be a Discord user id (17 to 20 digits).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsDiscordWebhook(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && _webhookHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
}
