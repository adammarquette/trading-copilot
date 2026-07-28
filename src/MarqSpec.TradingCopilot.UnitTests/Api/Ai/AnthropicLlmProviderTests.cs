using System.Net;
using System.Text.Json.Nodes;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The real Anthropic adapter (gh#423, A2 / ADR-0008). The design-defining behaviours: it POSTs the configured model
/// for the request's tier with the key on a <b>header</b> (never the body), wires the caller's JSON schema into
/// <c>output_config.format</c>, maps <c>stop_reason</c> (including a <b>refusal</b>) and usage, and is <b>fail-closed</b>
/// — a non-2xx status or an unparseable body throws (which the reviewer turns into a suppression), and a genuine
/// cancellation propagates. It never fabricates a completion.
/// </summary>
public class AnthropicLlmProviderTests
{
    private const string ApiKey = "sk-ant-secret-value-42";

    // A well-formed Messages-API answer the parser accepts (built via JsonObject to dodge brace-escaping).
    private static HttpResponseMessage Ok(
        string stopReason = "end_turn", string text = "hello back", int input = 11, int output = 7)
    {
        JsonObject body = new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = stopReason,
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output },
        };
        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static LlmRequest Request(
        LlmModelTier tier = LlmModelTier.Triage, LlmResponseFormat? format = null, int maxTokens = 512) =>
        new(tier, "sys", [new LlmMessage(LlmRole.User, "hello")], format ?? LlmResponseFormat.Text, maxTokens);

    private static AnthropicLlmProvider Provider(StubHandler handler, LlmOptions? options = null) =>
        new(new HttpClient(handler),
            Options.Create(options ?? new LlmOptions { ApiKey = ApiKey }),
            NullLogger<AnthropicLlmProvider>.Instance);

    // --- Tier -> model ---

    [Fact]
    public async Task CompleteAsync_ShouldPostTheTriageModel_WhenTierIsTriage()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(Request(LlmModelTier.Triage), CancellationToken.None);

        Body(handler)["model"]!.GetValue<string>().Should().Be("claude-haiku-4-5"); // the default triage model
    }

    [Fact]
    public async Task CompleteAsync_ShouldPostTheDeepModel_WhenTierIsDeep()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(Request(LlmModelTier.Deep), CancellationToken.None);

        Body(handler)["model"]!.GetValue<string>().Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task CompleteAsync_ShouldUseTheOperatorsConfiguredModels()
    {
        StubHandler handler = new(_ => Ok());
        LlmOptions options = new() { ApiKey = ApiKey, TriageModel = "claude-opus-5" };
        await Provider(handler, options).CompleteAsync(Request(LlmModelTier.Triage), CancellationToken.None);

        Body(handler)["model"]!.GetValue<string>().Should().Be("claude-opus-5");
    }

    // --- The prompt, messages, and max_tokens are carried through ---

    [Fact]
    public async Task CompleteAsync_ShouldCarryTheSystemPromptMessagesAndTokenCeiling()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(Request(maxTokens: 512), CancellationToken.None);

        JsonNode body = Body(handler);
        body["system"]!.GetValue<string>().Should().Be("sys");
        body["max_tokens"]!.GetValue<int>().Should().Be(512);
        JsonNode message = body["messages"]![0]!;
        message["role"]!.GetValue<string>().Should().Be("user");
        message["content"]!.GetValue<string>().Should().Be("hello");
    }

    // --- The key rides a header, never the body ---

    [Fact]
    public async Task CompleteAsync_ShouldSendTheKeyOnAHeaderAndNeverInTheBody()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        handler.LastRequest!.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
        handler.LastRequest.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
        handler.LastBody.Should().NotContain(ApiKey, "the API key must never appear in the request body");
    }

    // --- JSON-schema wiring ---

    [Fact]
    public async Task CompleteAsync_ShouldWireTheSchemaIntoOutputConfig_WhenAJsonFormatIsRequested()
    {
        StubHandler handler = new(_ => Ok());
        LlmResponseFormat format = LlmResponseFormat.Json("""{"type":"object","properties":{"x":{"type":"string"}}}""");
        await Provider(handler).CompleteAsync(Request(format: format), CancellationToken.None);

        JsonNode outputFormat = Body(handler)["output_config"]!["format"]!;
        outputFormat["type"]!.GetValue<string>().Should().Be("json_schema");
        // The schema is embedded as an OBJECT (not a re-stringified string), so the provider can read it.
        outputFormat["schema"]!["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public async Task CompleteAsync_ShouldOmitOutputConfig_WhenTextFormatIsRequested()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(Request(format: LlmResponseFormat.Text), CancellationToken.None);

        Body(handler)["output_config"].Should().BeNull();
    }

    // --- Stop-reason mapping ---

    [Theory]
    [InlineData("end_turn", LlmStopReason.Completed)]
    [InlineData("stop_sequence", LlmStopReason.Completed)]
    [InlineData("max_tokens", LlmStopReason.MaxTokens)]
    [InlineData("refusal", LlmStopReason.Refusal)]
    [InlineData("tool_use", LlmStopReason.Other)]
    [InlineData("something_new", LlmStopReason.Other)]
    public async Task CompleteAsync_ShouldMapTheStopReason(string wire, LlmStopReason expected)
    {
        StubHandler handler = new(_ => Ok(stopReason: wire));
        LlmCompletion completion = await Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        completion.StopReason.Should().Be(expected);
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnTheTextAndUsage()
    {
        StubHandler handler = new(_ => Ok(text: "the answer", input: 42, output: 9));
        LlmCompletion completion = await Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        completion.Text.Should().Be("the answer");
        completion.Usage.InputTokens.Should().Be(42);
        completion.Usage.OutputTokens.Should().Be(9);
    }

    [Fact]
    public async Task CompleteAsync_ShouldConcatenateTextBlocks_AndIgnoreNonTextBlocks()
    {
        JsonObject body = new()
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "a" },
                new JsonObject { ["type"] = "thinking", ["thinking"] = "x" },
                new JsonObject { ["type"] = "text", ["text"] = "b" }),
            ["stop_reason"] = "end_turn",
            ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
        };
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, body.ToJsonString()));
        LlmCompletion completion = await Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        completion.Text.Should().Be("ab");
    }

    // --- Fail-closed: a non-2xx or an unparseable body throws, never fabricates a completion ---

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CompleteAsync_ShouldThrow_WhenTheStatusIsNotSuccess(HttpStatusCode status)
    {
        StubHandler handler = new(_ => Json(status, """{"type":"error","error":{"message":"nope"}}"""));

        Func<Task> act = () => Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<AnthropicLlmException>();
    }

    [Fact]
    public async Task CompleteAsync_ShouldThrow_WhenA200BodyIsUnparseable()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, "this is not json"));

        Func<Task> act = () => Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<AnthropicLlmException>();
    }

    [Fact]
    public async Task CompleteAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        StubHandler handler = new(_ => Ok());

        Func<Task> act = () => Provider(handler).CompleteAsync(Request(), cts.Token);

        // Our own shutdown is not a "provider unavailable" -- it propagates, never wrapped as an AnthropicLlmException.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static JsonNode Body(StubHandler handler)
    {
        handler.LastBody.Should().NotBeNull();
        return JsonNode.Parse(handler.LastBody!)!;
    }

    /// <summary>A no-network <see cref="HttpMessageHandler"/> that captures the request + body and returns a canned reply.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
