using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the connection-loss orphan-handling suite (gh#192). Two test-only intercepts on top of the
/// adversarial venue stub. (1) It <b>strips every always-on <see cref="IHostedService"/></b> — most of all the
/// <c>VenueConnectionMonitorHost</c>, whose 5-second poll would call <c>OrphanGuardService.OrphanAsync</c> the
/// moment it observed the (never-connected) stub venue and orphan the suite's seeded hidden stops out from under
/// it; the suite instead drives <c>OrphanAsync</c> / <c>RearmAsync</c> / <c>PromoteForQuoteAsync</c> directly at a
/// chosen instant, the same deterministic pattern the flatten suites use (gh#186/#188). (2) It captures the log
/// stream so the high-severity <c>synthetic_risk</c> marker (a log today; the queryable audit row is gh#220) can
/// be asserted.
/// </summary>
public sealed class OrphanTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The captured log stream — asserted for the <c>synthetic_risk</c> emergency marker.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host: the only orphan / re-arm / promotion pass is the test's own explicit call.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}

/// <summary>One captured log record, including the <b>category</b> that emitted it.</summary>
/// <param name="Category">The logger category — normally the emitting type's full name.</param>
/// <param name="Level">The severity.</param>
/// <param name="Message">The formatted message.</param>
public sealed record CapturedLog(string Category, LogLevel Level, string Message);

/// <summary>An in-memory sink for captured log records — the suite asserts on level + message text.</summary>
public sealed class InMemoryLogCollector
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    /// <summary>Records one log entry whose category is not of interest to the caller.</summary>
    public void Add(LogLevel level, string message) => Add(string.Empty, level, message);

    /// <summary>Records one log entry with the category that emitted it.</summary>
    public void Add(string category, LogLevel level, string message) =>
        _entries.Enqueue(new CapturedLog(category, level, message));

    /// <summary>A snapshot of everything captured so far, level + message only.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries =>
        [.. _entries.Select(entry => (entry.Level, entry.Message))];

    /// <summary>
    /// A snapshot including each record's category. Needed wherever a factory keeps <b>several</b> hosted services
    /// running: an unscoped "nothing at Error" assertion is then failed by any unrelated consumer, which makes a red
    /// mean something other than the defect under test (gh#632 review).
    /// </summary>
    public IReadOnlyList<CapturedLog> Records => [.. _entries];

    /// <summary>Drops everything captured — call at the start of each test for isolation.</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>A logger provider that funnels every category's records into a shared <see cref="InMemoryLogCollector"/>.</summary>
internal sealed class CapturingLoggerProvider(InMemoryLogCollector collector) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(collector, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(InMemoryLogCollector collector, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            collector.Add(category, logLevel, formatter(state, exception));
        }
    }
}
