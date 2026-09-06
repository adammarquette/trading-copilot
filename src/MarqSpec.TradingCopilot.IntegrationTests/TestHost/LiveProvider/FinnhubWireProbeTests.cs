using System.Net;
using static MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider.FinnhubWireProbe;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// Proves the gh#1130 by-construction distinction in <see cref="FinnhubWireProbe"/>: a <b>429</b> must surface
/// as <see cref="FinnhubRateLimitedException"/>, never as the same opaque transport failure any other refusal
/// produces — the suite's own outage case (<c>ARefusedProvider_ShouldNotSinkTheOther</c>) depends on a reader
/// being able to tell "this run was rate-limited" from "the provider is down".
/// </summary>
/// <remarks>
/// Exercised against a fake <see cref="HttpMessageHandler"/> via the internal <c>FetchAsync</c> seam — no
/// network, no API key — so it runs pre-merge on every PR rather than skipping like the live-provider cases.
/// Paired with a control on an unrelated failure status: without it, always throwing
/// <see cref="FinnhubRateLimitedException"/> regardless of status would make the first assertion pass too.
/// </remarks>
public sealed class FinnhubWireProbeTests
{
    [Fact]
    public async Task FetchAsync_ShouldThrowARateLimitException_WhenFinnhubAnswers429()
    {
        // FAILURE MODE (the defect this guards against): EnsureSuccessStatusCode() turns a 429 into a generic
        // HttpRequestException indistinguishable from any other transport failure, so a rate-limited run reads
        // as a provider outage. Prove-red: reverting FetchAsync to call EnsureSuccessStatusCode() unconditionally
        // makes this throw HttpRequestException instead, and the assertion below goes red.
        using StubHandler handler = new(HttpStatusCode.TooManyRequests, "{\"error\":\"limit exceeded\"}");

        Func<Task> fetch = () => FetchAsync("irrelevant-key", handler, CancellationToken.None);

        (await fetch.Should().ThrowAsync<FinnhubRateLimitedException>(
                "a 429 must be legible as rate-limiting, not an opaque transport failure"))
            .WithMessage("*429*");
    }

    [Fact]
    public async Task FetchAsync_ShouldThrowTheGenericTransportException_WhenFinnhubAnswersAnUnrelatedFailure()
    {
        // The control: an UNRELATED failure status must still surface as the ordinary transport exception, not
        // FinnhubRateLimitedException — otherwise the case above could pass by mislabelling every failure as
        // rate-limiting rather than actually distinguishing the 429 case.
        using StubHandler handler = new(HttpStatusCode.InternalServerError, "boom");

        Func<Task> fetch = () => FetchAsync("irrelevant-key", handler, CancellationToken.None);

        await fetch.Should().ThrowExactlyAsync<HttpRequestException>(
            "a non-429 failure is an ordinary transport error, not rate-limiting");
    }

    /// <summary>Answers every request with a fixed status and body, so the 429 branch is reachable without a network call.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
