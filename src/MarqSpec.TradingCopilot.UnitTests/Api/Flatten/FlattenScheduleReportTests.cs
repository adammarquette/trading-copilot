using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// Reporting the effective auto-flatten schedule at startup (gh#255, R-13). The safety-relevant behaviour: an
/// operator must be able to read the log and tell <b>which deadlines are armed</b> and <b>where each value came
/// from</b> — because a dropped override and a built-in default look identical at runtime, which is exactly how
/// gh#236 hid for as long as it did.
/// </summary>
public class FlattenScheduleReportTests
{
    private static readonly DateTimeOffset _summerNoonUtc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _winterNoonUtc = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static FlattenScheduleReport For(FlattenOptions options, string symbol, DateTimeOffset? now = null) =>
        options.Describe(now ?? _summerNoonUtc).Single(report => report.Instrument == InstrumentId.Parse(symbol));

    [Fact]
    public void Describe_ShouldReportEveryGovernedMarketAsBuiltInDefault_WhenNothingIsConfigured()
    {
        // The out-of-the-box posture (R-13): armed for the core products, every one of them a built-in default.
        IReadOnlyList<FlattenScheduleReport> reports = new FlattenOptions().Describe(_summerNoonUtc);

        reports.Select(report => report.Instrument.Symbol)
            .Should().BeEquivalentTo(["ES", "NQ", "CL", "GC"]);
        reports.Should().OnlyContain(report => report.Source == FlattenScheduleSource.BuiltInDefault);
        reports.Should().OnlyContain(report => report.Enabled);
    }

    [Fact]
    public void Describe_ShouldReportConfiguredOverride_WhenAnEntryMovesTheDeadline()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:15" }],
        };

        FlattenScheduleReport es = For(options, "ES");

        es.Deadline.Should().Be(new TimeOnly(14, 15));
        es.Source.Should().Be(FlattenScheduleSource.ConfiguredOverride);
    }

    [Fact]
    public void Describe_ShouldStillReportConfiguredOverride_WhenTheConfiguredTimeMatchesTheBuiltInDefault()
    {
        // THE point of the report. "ES 14:30" alone cannot distinguish an override that took from a default that
        // silently replaced it -- which is the failure gh#236 was. The SOURCE is what carries that information,
        // so a configured value must read as configured even when it lands on the same time.
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:30" }],
        };

        FlattenScheduleReport es = For(options, "ES");

        es.Deadline.Should().Be(new TimeOnly(14, 30));
        es.Source.Should().Be(FlattenScheduleSource.ConfiguredOverride);
    }

    [Fact]
    public void Describe_ShouldReportConfiguredAddition_WhenAnEntryAddsAMarketOutsideTheBuiltIns()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "MES", SessionClose = "14:30" }],
        };

        For(options, "MES").Source.Should().Be(FlattenScheduleSource.ConfiguredAddition);

        // ...and the built-ins are untouched beside it.
        For(options, "ES").Source.Should().Be(FlattenScheduleSource.BuiltInDefault);
    }

    [Fact]
    public void Describe_ShouldReportDisabled_WhenAMarketIsDeliberatelyTurnedOff()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", Enabled = false }],
        };

        FlattenScheduleReport es = For(options, "ES");

        es.Enabled.Should().BeFalse();
        es.Source.Should().Be(FlattenScheduleSource.ConfiguredOverride);
    }

    [Fact]
    public void Describe_ShouldPreferTheDeadlineOverride_WhenBothItAndSessionCloseAreConfigured()
    {
        FlattenOptions options = new()
        {
            Instruments =
            [
                new FlattenScheduleOption { Symbol = "ES", SessionClose = "15:00", DeadlineOverride = "13:45" },
            ],
        };

        For(options, "ES").Deadline.Should().Be(new TimeOnly(13, 45));
    }

    [Fact]
    public void Describe_ShouldResolveTheDeadlineToUtcAcrossDaylightSaving_WhenTheDateChanges()
    {
        // The DST footgun this report exists to expose: the SAME 14:30 CT deadline is a DIFFERENT UTC instant in
        // summer (CDT, UTC-5) and winter (CST, UTC-6). A deadline pinned to an offset drifts an hour twice a year
        // and lands after the close it exists to beat (MarketClock's own remark).
        FlattenScheduleReport summer = For(new FlattenOptions(), "ES", _summerNoonUtc);
        FlattenScheduleReport winter = For(new FlattenOptions(), "ES", _winterNoonUtc);

        summer.Deadline.Should().Be(winter.Deadline);

        summer.DeadlineUtc.Should().Be(new DateTimeOffset(2026, 7, 15, 19, 30, 0, TimeSpan.Zero));
        winter.DeadlineUtc.Should().Be(new DateTimeOffset(2026, 1, 15, 20, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Report_ShouldLogOneRecordPerGovernedMarket_WhenTheScheduleIsReported()
    {
        CapturingLogger logger = new();

        new FlattenScheduleReporter(Options.Create(new FlattenOptions()), logger).Report(_summerNoonUtc);

        logger.Entries.Should().HaveCount(4);
    }

    [Fact]
    public void Report_ShouldLogAtInformation_WhenAMarketIsArmed()
    {
        CapturingLogger logger = new();

        new FlattenScheduleReporter(Options.Create(new FlattenOptions()), logger).Report(_summerNoonUtc);

        logger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public void Report_ShouldLogAboveInformation_WhenAMarketIsDisabled()
    {
        // Disabling is the deliberate, warned override (R-13). It must never scroll past as routine Information --
        // an unarmed market is the one line in this report an operator cannot afford to miss.
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", Enabled = false }],
        };
        CapturingLogger logger = new();

        new FlattenScheduleReporter(Options.Create(options), logger).Report(_summerNoonUtc);

        logger.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Warning)
            .Which.Message.Should().Contain("ES");
        logger.Entries.Count(entry => entry.Level == LogLevel.Information).Should().Be(3);
    }

    /// <summary>An in-memory <see cref="ILogger"/> — the suite asserts on level + rendered message, no I/O.</summary>
    private sealed class CapturingLogger : ILogger<FlattenScheduleReporter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
