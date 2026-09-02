using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A genuine local HTTP server standing in for Cohere's <c>/v2/rerank</c> endpoint (gh#976, of gh#975) — a real
/// Kestrel listener on loopback, never a fake <see cref="HttpMessageHandler"/>. <see cref="CohereRerankProvider"/>
/// is driven against it over an actual socket, so the real request serialization, header shape, and response
/// deserialization all run for real; only the far end (Cohere itself) is stood in for, exactly as the QA "nothing
/// mocked" discipline permits for an outbound third-party seam that cannot exist pre-merge.
/// </summary>
public sealed class StubCohereRerankServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Queue<(HttpStatusCode Status, string Body)> _script = [];
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>One request the stub observed: its raw JSON body and its raw <c>Authorization</c> header value.</summary>
    public sealed record RecordedRequest(string Body, string? Authorization);

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The stub's base address — set this as <c>Cohere:BaseUrl</c> to point the real provider at it.</summary>
    public string BaseUrl { get; }

    private StubCohereRerankServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>Queues the next response the stub returns, in call order; unscripted calls get a bare 200 <c>{}</c>.</summary>
    public void Script(HttpStatusCode status, string body)
    {
        lock (_gate)
        {
            _script.Enqueue((status, body));
        }
    }

    /// <summary>Starts the stub on an OS-assigned loopback port.</summary>
    public static async Task<StubCohereRerankServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep the real network round trip, not its log noise
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();

        StubCohereRerankServer? server = null;
        app.MapPost("/v2/rerank", async context =>
        {
            using StreamReader reader = new(context.Request.Body);
            string body = await reader.ReadToEndAsync();
            string? authorization = context.Request.Headers.TryGetValue("Authorization", out Microsoft.Extensions.Primitives.StringValues value)
                ? value.ToString()
                : null;

            (HttpStatusCode status, string responseBody) script;
            lock (server!._gate)
            {
                server._requests.Add(new RecordedRequest(body, authorization));
                script = server._script.Count > 0 ? server._script.Dequeue() : (HttpStatusCode.OK, "{}");
            }

            context.Response.StatusCode = (int)script.status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(script.responseBody);
        });

        await app.StartAsync();

        string boundAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        server = new StubCohereRerankServer(app, boundAddress);
        return server;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
