using MarqSpec.TradingCopilot.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.Http;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// A Discord webhook URL carries its token in the request <b>path</b>, and the HTTP client instrumentation records
/// the full URL on the span it exports (gh#1157). The credential must not reach a trace backend, so those
/// requests are not traced at all.
/// </summary>
public class HttpTraceCredentialFilterTests
{
    private static Func<HttpRequestMessage, bool> Filter()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotTelemetry();
        using WebApplication app = builder.Build();

        return app.Services.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName).FilterHttpRequestMessage!;
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/111111111111111111/fake-token-do-not-use")]
    [InlineData("https://discordapp.com/api/webhooks/111111111111111111/fake-token-do-not-use")]
    [InlineData("https://ptb.discord.com/api/webhooks/111111111111111111/fake-token-do-not-use")]
    public void FilterHttpRequestMessage_ShouldNotTrace_WhenTheRequestIsADiscordWebhook(string url)
    {
        Filter()(new HttpRequestMessage(HttpMethod.Post, url)).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://api.pushover.net/1/messages.json")]
    [InlineData("https://discord.com/api/v10/users/@me/channels")]
    [InlineData("https://example.com/api/webhooks/1/x")]
    public void FilterHttpRequestMessage_ShouldStillTrace_EverythingElse(string url)
    {
        Filter()(new HttpRequestMessage(HttpMethod.Post, url)).Should().BeTrue();
    }
}
