using MarqSpec.TradingCopilot.Api.Notifications;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// Startup validation of the Discord options (gh#1157). Keyless is a legal, quiet configuration; a
/// <i>half-set</i> or malformed one is a mistake that must stop the host at boot, not surface as a page that
/// silently never arrives. The messages name the key, never the value.
/// </summary>
public class DiscordOptionsValidatorTests
{
    private const string GoodWebhook = "https://discord.com/api/webhooks/111111111111111111/fake-token-do-not-use";
    private const string GoodUser = "222222222222222222";

    private static ValidateOptionsResult Validate(DiscordOptions options) =>
        new DiscordOptionsValidator().Validate(null, options);

    [Fact]
    public void Validate_ShouldSucceed_WhenNothingIsSet()
    {
        Validate(new DiscordOptions()).Succeeded.Should().BeTrue("a keyless host is allowed; it just has no Discord");
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenAllThreeAreBlankStrings()
    {
        // compose forwards an unset variable as an empty string through some paths.
        Validate(new DiscordOptions { WebhookUrl = "", BotToken = " ", OperatorUserId = "" })
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenOnlyAWebhookIsSet()
    {
        Validate(new DiscordOptions { WebhookUrl = GoodWebhook }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenTheBotTokenAndOperatorArePaired()
    {
        Validate(new DiscordOptions { BotToken = "fake-bot", OperatorUserId = GoodUser }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenEverythingIsSet()
    {
        Validate(new DiscordOptions { WebhookUrl = GoodWebhook, BotToken = "fake-bot", OperatorUserId = GoodUser })
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFailNamingTheOperatorKey_WhenABotTokenHasNoRecipient()
    {
        ValidateOptionsResult result = Validate(new DiscordOptions { BotToken = "fake-bot-secret" });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Discord:OperatorUserId");
        string.Join(' ', result.Failures!).Should().NotContain("fake-bot-secret");
    }

    [Fact]
    public void Validate_ShouldFailNamingTheBotTokenKey_WhenARecipientHasNoBotToken()
    {
        ValidateOptionsResult result = Validate(new DiscordOptions { OperatorUserId = GoodUser });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Discord:BotToken");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("12345")]
    [InlineData("222222222222222222 ")]
    [InlineData("22222222222222222x")]
    public void Validate_ShouldFail_WhenTheOperatorIdIsNotASnowflake(string userId)
    {
        ValidateOptionsResult result = Validate(new DiscordOptions { BotToken = "fake-bot", OperatorUserId = userId });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Discord:OperatorUserId");
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://evil.example.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://discord.com.evil.example.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://discord.com/api/v10/channels/1/messages")]
    [InlineData("https://discord.com/")]
    [InlineData("discord.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("not a url")]
    public void Validate_ShouldFail_WhenTheWebhookIsNotAnHttpsDiscordWebhookUrl(string webhook)
    {
        ValidateOptionsResult result = Validate(new DiscordOptions { WebhookUrl = webhook });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Discord:WebhookUrl");
        string.Join(' ', result.Failures!).Should().NotContain("fake-token", "a webhook URL is a credential");
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://discordapp.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://ptb.discord.com/api/webhooks/111111111111111111/fake-token")]
    [InlineData("https://canary.discord.com/api/webhooks/111111111111111111/fake-token")]
    public void Validate_ShouldAcceptTheDocumentedDiscordWebhookHosts(string webhook)
    {
        Validate(new DiscordOptions { WebhookUrl = webhook }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldReportEveryProblemAtOnce_SoTheOperatorFixesThemInOnePass()
    {
        ValidateOptionsResult result = Validate(new DiscordOptions { WebhookUrl = "http://x", BotToken = "fake-bot" });

        result.Failures.Should().HaveCount(2);
    }
}
