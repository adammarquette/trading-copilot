namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// The operator's Discord delivery targets (gh#1157, ADR-0019). Bound from the <c>Discord</c> config section.
/// </summary>
/// <remarks>
/// <para>
/// All three values are <b>secrets</b> — environment only, never source, never logged. A webhook URL carries its
/// own token in the path, so it is as sensitive as the bot token.
/// </para>
/// <para>
/// The destination is selected by which values are present, not by a separate switch: a webhook posts to a
/// <b>channel</b>, a bot token plus the operator's user id sends a <b>direct message</b>, and both together do
/// both. Nothing set means no Discord transport at all.
/// </para>
/// </remarks>
public sealed class DiscordOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Discord";

    /// <summary>The channel webhook URL; posting to it delivers to that channel.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>The bot token used to open and post into the operator's direct-message channel.</summary>
    public string? BotToken { get; init; }

    /// <summary>The operator's Discord user id (a snowflake) — the only recipient a DM is ever sent to.</summary>
    public string? OperatorUserId { get; init; }

    /// <summary>Whether a channel webhook is set.</summary>
    public bool HasWebhook => !string.IsNullOrWhiteSpace(WebhookUrl);

    /// <summary>Whether both halves of the direct-message pair are set.</summary>
    public bool HasDirectMessage =>
        !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(OperatorUserId);

    /// <summary>Whether at least one destination is set.</summary>
    public bool IsConfigured => HasWebhook || HasDirectMessage;
}
