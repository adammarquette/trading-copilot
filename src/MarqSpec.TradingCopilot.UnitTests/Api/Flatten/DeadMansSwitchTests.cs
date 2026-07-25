using System.Net;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// The outbound half of the dead-man's switch (gh#244, ADR-0019): reporting flat, and reporting alive, to a
/// monitor that lives outside this process.
/// </summary>
/// <remarks>
/// Two properties matter more than the happy path. It must <b>never throw or hang into the caller</b> — this runs
/// alongside the one autonomous action the system takes, and an alerting fault that breaks trading would be a
/// self-inflicted wound of exactly the kind ADR-0019 warns about. And it must <b>never log the ping URL</b>: those
/// are capability URLs, so anyone holding one can forge an all-clear.
/// </remarks>
public class DeadMansSwitchTests
{
    private const string EsUrl = "https://hc.example/ping/es-secret-token";
    private const string HeartbeatUrl = "https://hc.example/ping/alive-secret-token";

    private static CheckInOptions Configured()
    {
        return new CheckInOptions
        {
            HeartbeatUrl = HeartbeatUrl,
            Instruments = [new CheckInUrlOption { Symbol = "ES", Url = EsUrl }],
        };
    }

    private static HttpDeadMansSwitch Switch(CheckInOptions options, StubHandler handler)
    {
        return new HttpDeadMansSwitch(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<HttpDeadMansSwitch>.Instance);
    }

    private static InstrumentId Es => InstrumentId.Parse("ES");

    // --- Reporting flat ---

    [Fact]
    public async Task ReportFlatAsync_ShouldPingTheInstrumentsCheck_WhenConfigured()
    {
        StubHandler handler = new();

        bool sent = await Switch(Configured(), handler).ReportFlatAsync(Es, CancellationToken.None);

        sent.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri.Should().Be(new Uri(EsUrl));
    }

    [Fact]
    public async Task ReportFlatAsync_ShouldNotSend_WhenTheInstrumentHasNoConfiguredCheck()
    {
        StubHandler handler = new();

        bool sent = await Switch(Configured(), handler).ReportFlatAsync(InstrumentId.Parse("NQ"), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportFlatAsync_ShouldReturnFalse_WhenTheMonitorRejectsThePing()
    {
        StubHandler handler = new() { Status = HttpStatusCode.InternalServerError };

        bool sent = await Switch(Configured(), handler).ReportFlatAsync(Es, CancellationToken.None);

        // A failed check-in is not a lie -- it is a check-in that did not happen, and the monitor will page.
        sent.Should().BeFalse();
    }

    [Fact]
    public async Task ReportFlatAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestFaults()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("no route to host") };

        bool sent = await Switch(Configured(), handler).ReportFlatAsync(Es, CancellationToken.None);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task ReportFlatAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestTimesOut()
    {
        // A hung monitor must not become a hung safety path.
        StubHandler handler = new() { Throw = new TaskCanceledException("timeout") };

        bool sent = await Switch(Configured(), handler).ReportFlatAsync(Es, CancellationToken.None);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task ReportFlatAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        // The timeout swallow above must not also swallow a host shutdown -- that would stop the loop exiting.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        StubHandler handler = new() { Throw = new TaskCanceledException("stopping") };

        Func<Task> act = () => Switch(Configured(), handler).ReportFlatAsync(Es, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Reporting alive ---

    [Fact]
    public async Task ReportAliveAsync_ShouldPingTheHeartbeatCheck_WhenConfigured()
    {
        StubHandler handler = new();

        bool sent = await Switch(Configured(), handler).ReportAliveAsync(CancellationToken.None);

        sent.Should().BeTrue();
        handler.Requests[0].RequestUri.Should().Be(new Uri(HeartbeatUrl));
    }

    [Fact]
    public async Task ReportAliveAsync_ShouldNotSend_WhenNoHeartbeatUrlIsConfigured()
    {
        StubHandler handler = new();
        CheckInOptions unconfigured = new();

        bool sent = await Switch(unconfigured, handler).ReportAliveAsync(CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    // --- The URL is a secret ---

    [Fact]
    public async Task ReportFlatAsync_ShouldKeepTheUrlOutOfTheLog_WhenThePingFails()
    {
        // A ping URL is a capability URL: whoever holds it can forge an all-clear on a safety path.
        CapturingLogger logger = new();
        HttpDeadMansSwitch subject = new(
            new HttpClient(new StubHandler { Throw = new HttpRequestException("boom") }),
            Options.Create(Configured()),
            logger);

        await subject.ReportFlatAsync(Es, CancellationToken.None);

        logger.Messages.Should().NotBeEmpty();
        string logged = string.Join("\n", logger.Messages);
        logged.Should().NotContain("es-secret-token");
        logged.Should().Contain("ES"); // the instrument is still identifiable
    }

    // --- Doubles ---

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Exception? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Throw is not null
                ? Task.FromException<HttpResponseMessage>(Throw)
                : Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private sealed class CapturingLogger : ILogger<HttpDeadMansSwitch>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, error));
        }
    }
}
