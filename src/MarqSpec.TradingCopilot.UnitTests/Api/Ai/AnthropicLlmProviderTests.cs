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

    // A request with a custom message list and/or offered tools -- the tool-serialization + extraction tests (inc 4).
    private static LlmRequest RequestWith(
        IReadOnlyList<LlmMessage>? messages = null, IReadOnlyList<LlmToolDefinition>? tools = null) =>
        new(LlmModelTier.Deep, "sys", messages ?? [new LlmMessage(LlmRole.User, "hello")], LlmResponseFormat.Text, 512, tools);

    // A read-only tool definition, name/description/JSON-Schema -- shaped exactly like the ones IChatTool will offer.
    private static readonly LlmToolDefinition SampleTool = new(
        "get_quote",
        "Get the latest quote for a contract.",
        """{"type":"object","properties":{"symbol":{"type":"string"}},"required":["symbol"]}""");

    // A well-formed Messages-API answer in which the model asks to call get_quote (a text block, then a tool_use block).
    private static HttpResponseMessage ToolUseResponse(string text = "let me look that up") =>
        Json(HttpStatusCode.OK, new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_99",
                    ["name"] = "get_quote",
                    ["input"] = new JsonObject { ["symbol"] = "ESU5" },
                }),
            ["stop_reason"] = "tool_use",
            ["usage"] = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 15 },
        }.ToJsonString());

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
    [InlineData("tool_use", LlmStopReason.ToolUse)] // the model wants a tool -- its own reason now (gh#906 inc 4)
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

    [Theory]
    [InlineData("[]")]              // a JSON array
    [InlineData("\"warming up\"")]  // a JSON string
    [InlineData("42")]              // a JSON number
    public async Task CompleteAsync_ShouldThrow_WhenA200BodyIsValidJsonButNotAnObject(string body)
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, body));

        Func<Task> act = () => Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        // The contract is total: a well-formed-JSON-but-wrong-shape 2xx fails closed as AnthropicLlmException, not a
        // stray InvalidOperationException from a JsonElement accessor.
        await act.Should().ThrowAsync<AnthropicLlmException>();
    }

    [Theory]
    // A scalar content-array element, and a text block whose "text" is a number: both are junk the extractor must
    // SKIP without crashing an accessor -- yielding empty text, which the reviewer then fails closed on downstream.
    // (This is the same "ignore anything that isn't a well-formed text block" posture as a non-"text" block type.)
    [InlineData("""{"content":[42],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"content":[{"type":"text","text":123}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""")]
    public async Task CompleteAsync_ShouldSkipAJunkContentBlock_AndReturnNoTextForIt(string body)
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, body));

        LlmCompletion completion = await Provider(handler).CompleteAsync(Request(), CancellationToken.None);

        completion.Text.Should().BeEmpty();
        completion.StopReason.Should().Be(LlmStopReason.Completed);
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

    // --- Streaming (gh#906 inc 3b): the SSE parser surfaces deltas in order and maps the final stop reason + usage ---

    [Fact]
    public async Task StreamAsync_ShouldSurfaceEachTextDeltaInOrder_AndMapTheFinalStopReasonAndUsage()
    {
        StubHandler handler = new(_ => Sse(
            MessageStart(input: 11, output: 1),
            TextDelta("Hello"),
            TextDelta(" world"),
            MessageDelta("end_turn", output: 7),
            new JsonObject { ["type"] = "message_stop" }));

        (LlmCompletion completion, List<string> deltas) = await Stream(handler);

        deltas.Should().ContainInOrder("Hello", " world"); // streamed token-by-token, in order
        completion.Text.Should().Be("Hello world"); // and accumulated to the full answer
        completion.StopReason.Should().Be(LlmStopReason.Completed);
        // input from message_start; output is the cumulative count on the final message_delta, not the initial 1.
        completion.Usage.Should().Be(new LlmUsage(11, 7));
    }

    [Fact]
    public async Task StreamAsync_ShouldRequestStreaming_InTheBody()
    {
        StubHandler handler = new(_ => Sse(MessageStart(1, 1), MessageDelta("end_turn", 1)));

        await Stream(handler);

        Body(handler)["stream"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ShouldMapARefusalStop_SoTheCallerFailsClosed()
    {
        StubHandler handler = new(_ => Sse(MessageStart(1, 1), TextDelta("no"), MessageDelta("refusal", 1)));

        (LlmCompletion completion, _) = await Stream(handler);

        completion.StopReason.Should().Be(LlmStopReason.Refusal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task StreamAsync_ShouldThrow_WhenTheStatusIsNotSuccess(HttpStatusCode status)
    {
        StubHandler handler = new(_ => Json(status, """{"type":"error"}"""));

        Func<Task> act = () => Stream(handler);

        await act.Should().ThrowAsync<AnthropicLlmException>(); // fail-closed, exactly as CompleteAsync
    }

    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenADataLineIsNotValidJson()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: this is not json\n\n", System.Text.Encoding.UTF8, "text/event-stream"),
        });

        Func<Task> act = () => Stream(handler);

        await act.Should().ThrowAsync<AnthropicLlmException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenTheStreamCarriesAnErrorEvent()
    {
        StubHandler handler = new(_ => Sse(
            MessageStart(1, 1),
            new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "overloaded" } }));

        Func<Task> act = () => Stream(handler);

        await act.Should().ThrowAsync<AnthropicLlmException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        StubHandler handler = new(_ => Sse(MessageStart(1, 1)));

        Func<Task> act = () => Provider(handler).StreamAsync(Request(), (_, _) => Task.CompletedTask, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(); // our shutdown, never wrapped
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreANonTextBlockDelta_SoOnlyTheAnswerSurfaces()
    {
        // A thinking / tool-input block delta is not the answer -- it must never be forwarded or accumulated (gh#922 review).
        StubHandler handler = new(_ => Sse(
            MessageStart(1, 1),
            new JsonObject { ["type"] = "content_block_delta", ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "hmm" } },
            TextDelta("answer"),
            MessageDelta("end_turn", 3)));

        (LlmCompletion completion, List<string> deltas) = await Stream(handler);

        deltas.Should().ContainSingle().Which.Should().Be("answer");
        completion.Text.Should().Be("answer");
    }

    [Fact]
    public async Task StreamAsync_ShouldMapATruncatedStreamWithNoStopReason_ToOther_SoTheCallerFailsClosed()
    {
        // A stream that ends after a delta with no message_delta (a dropped connection mid-turn) carries no
        // stop_reason -- so it maps to Other, which ChatTurnService fails closed on, never surfacing a partial
        // answer as a complete one (gh#922 review).
        StubHandler handler = new(_ => Sse(MessageStart(1, 1), TextDelta("half an ans")));

        (LlmCompletion completion, _) = await Stream(handler);

        completion.StopReason.Should().Be(LlmStopReason.Other);
    }

    // --- Tools (gh#906 inc 4): serialize offered tools + tool-use/tool-result turns; extract the model's tool_use ---

    [Fact]
    public async Task CompleteAsync_ShouldSerializeOfferedTools_AsTopLevelDefinitions()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(tools: [SampleTool]), CancellationToken.None);

        JsonNode tool = Body(handler)["tools"]![0]!;
        tool["name"]!.GetValue<string>().Should().Be("get_quote");
        tool["description"]!.GetValue<string>().Should().Be("Get the latest quote for a contract.");
        // The schema is embedded as an OBJECT (not a re-stringified string), so the provider can read it -- the same
        // posture as output_config.format above.
        tool["input_schema"]!["type"]!.GetValue<string>().Should().Be("object");
        tool["input_schema"]!["properties"]!["symbol"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public async Task CompleteAsync_ShouldOmitTools_WhenNoneAreOffered()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(), CancellationToken.None);

        Body(handler)["tools"].Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_ShouldOmitTools_WhenTheToolListIsEmpty()
    {
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(tools: []), CancellationToken.None);

        Body(handler)["tools"].Should().BeNull(); // an empty list is no tools, not an empty `tools: []`
    }

    [Fact]
    public async Task CompleteAsync_ShouldSerializeAnAssistantToolUseTurn_AsTextThenToolUseBlocks()
    {
        LlmMessage assistant = new(
            LlmRole.Assistant, "let me check", ToolCalls: [new LlmToolCall("toolu_1", "get_quote", """{"symbol":"ESU5"}""")]);
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(messages: [assistant]), CancellationToken.None);

        JsonArray content = Body(handler)["messages"]![0]!["content"]!.AsArray();
        content.Should().HaveCount(2);
        content[0]!["type"]!.GetValue<string>().Should().Be("text");
        content[0]!["text"]!.GetValue<string>().Should().Be("let me check");
        content[1]!["type"]!.GetValue<string>().Should().Be("tool_use");
        content[1]!["id"]!.GetValue<string>().Should().Be("toolu_1");
        content[1]!["name"]!.GetValue<string>().Should().Be("get_quote");
        // input embedded as an object the model can read, not a re-stringified string
        content[1]!["input"]!["symbol"]!.GetValue<string>().Should().Be("ESU5");
    }

    [Fact]
    public async Task CompleteAsync_ShouldOmitTheTextBlock_WhenAnAssistantToolUseTurnHasNoText()
    {
        LlmMessage assistant = new(
            LlmRole.Assistant, "", ToolCalls: [new LlmToolCall("toolu_1", "get_quote", """{"symbol":"ESU5"}""")]);
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(messages: [assistant]), CancellationToken.None);

        JsonArray content = Body(handler)["messages"]![0]!["content"]!.AsArray();
        content.Should().HaveCount(1); // just the tool_use block -- no empty text block
        content[0]!["type"]!.GetValue<string>().Should().Be("tool_use");
    }

    [Fact]
    public async Task CompleteAsync_ShouldSerializeAUserToolResultTurn_AsToolResultBlocks()
    {
        LlmMessage toolResult = new(
            LlmRole.User, "", ToolResults: [new LlmToolResult("toolu_1", """{"last":5123.25}""", IsError: false)]);
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(messages: [toolResult]), CancellationToken.None);

        JsonNode block = Body(handler)["messages"]![0]!["content"]![0]!;
        block["type"]!.GetValue<string>().Should().Be("tool_result");
        block["tool_use_id"]!.GetValue<string>().Should().Be("toolu_1");
        block["content"]!.GetValue<string>().Should().Be("""{"last":5123.25}""");
        block["is_error"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_ShouldMarkAToolResultAsError_WhenTheToolFailed()
    {
        LlmMessage toolResult = new(
            LlmRole.User, "", ToolResults: [new LlmToolResult("toolu_1", "no such tool", IsError: true)]);
        StubHandler handler = new(_ => Ok());
        await Provider(handler).CompleteAsync(RequestWith(messages: [toolResult]), CancellationToken.None);

        Body(handler)["messages"]![0]!["content"]![0]!["is_error"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_ShouldExtractTheToolCallAndMapToolUse_WhenTheModelRequestsAToolUse()
    {
        StubHandler handler = new(_ => ToolUseResponse());
        LlmCompletion completion =
            await Provider(handler).CompleteAsync(RequestWith(tools: [SampleTool]), CancellationToken.None);

        completion.StopReason.Should().Be(LlmStopReason.ToolUse);
        completion.ToolCalls.Should().ContainSingle();
        LlmToolCall call = completion.ToolCalls![0];
        call.Id.Should().Be("toolu_99");
        call.Name.Should().Be("get_quote");
        // the input is kept as raw JSON -- the caller (the tool loop) owns the parse, exactly like the completion Text
        JsonNode.Parse(call.InputJson)!["symbol"]!.GetValue<string>().Should().Be("ESU5");
    }

    [Fact]
    public async Task CompleteAsync_ShouldExtractBothTheTextAndTheToolCall()
    {
        StubHandler handler = new(_ => ToolUseResponse(text: "checking the quote"));
        LlmCompletion completion =
            await Provider(handler).CompleteAsync(RequestWith(tools: [SampleTool]), CancellationToken.None);

        completion.Text.Should().Be("checking the quote");
        completion.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("get_quote");
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnNoToolCalls_WhenTheModelAnswersInPlainText()
    {
        StubHandler handler = new(_ => Ok());
        LlmCompletion completion = await Provider(handler).CompleteAsync(RequestWith(), CancellationToken.None);

        completion.ToolCalls.Should().BeNull(); // null, not an empty list -- there was no tool_use block
    }

    [Fact]
    public async Task StreamAsync_ShouldMapTheToolUseStopReason_SoTheLoopFallsBackToCompleteAsync()
    {
        // 4a does not parse tool_use blocks from the stream -- it only surfaces the stop reason, so the tool loop
        // re-issues the round non-streamed (CompleteAsync) to read the calls (the round-1 double-call, removed in 4b).
        StubHandler handler = new(_ => Sse(MessageStart(1, 1), TextDelta("let me check"), MessageDelta("tool_use", 5)));

        (LlmCompletion completion, _) = await Stream(handler);

        completion.StopReason.Should().Be(LlmStopReason.ToolUse);
    }

    private static async Task<(LlmCompletion Completion, List<string> Deltas)> Stream(StubHandler handler)
    {
        List<string> deltas = [];
        LlmCompletion completion = await Provider(handler).StreamAsync(
            Request(), (delta, _) => { deltas.Add(delta); return Task.CompletedTask; }, CancellationToken.None);
        return (completion, deltas);
    }

    // Assembles a text/event-stream body from event objects (built via JsonObject to dodge brace-escaping).
    private static HttpResponseMessage Sse(params JsonObject[] events)
    {
        IEnumerable<string> lines = events.SelectMany(e => new[]
        {
            "event: " + e["type"]!.GetValue<string>(),
            "data: " + e.ToJsonString(),
            string.Empty,
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n", lines) + "\n", System.Text.Encoding.UTF8, "text/event-stream"),
        };
    }

    private static JsonObject MessageStart(int input, int output) => new()
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject { ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output } },
    };

    private static JsonObject TextDelta(string text) => new()
    {
        ["type"] = "content_block_delta",
        ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text },
    };

    private static JsonObject MessageDelta(string stopReason, int output) => new()
    {
        ["type"] = "message_delta",
        ["delta"] = new JsonObject { ["stop_reason"] = stopReason },
        ["usage"] = new JsonObject { ["output_tokens"] = output },
    };

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
