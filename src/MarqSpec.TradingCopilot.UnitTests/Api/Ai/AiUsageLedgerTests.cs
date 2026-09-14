using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The persisted AIUsage ledger (gh#431, ADR-0008 / ADR-0002): it writes one <see cref="AiUsageRecord"/> per AI call,
/// in its OWN owner-scoped context (R-20), and is FAIL-OPEN by contract — a write fault is logged and swallowed so a
/// ledger blip can never roll back the AI call it records; only the caller's own cancellation propagates. The
/// design-defining behaviours: every field maps under the entry owner's scope, another operator can never see it, a
/// failed write does not throw, and a caller cancellation is NOT swallowed.
/// </summary>
public class AiUsageLedgerTests
{
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    // A reading context scoped to an owner -- the R-20 filter matches only that owner's rows.
    private TradingCopilotDbContext Context(Guid asUser) => new(Options, new FixedUser(asUser));

    private static readonly Guid _firing = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AiUsageEntry Entry(Guid owner) => new(
        owner,
        new AiCallCost(
            AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
            42, 9, 0.0006m, TimeSpan.FromMilliseconds(1234)),
        "trace-abc",
        Now,
        _firing);

    private AiUsageLedger Ledger() => new(Options, NullLogger<AiUsageLedger>.Instance);

    [Fact]
    public async Task RecordAsync_ShouldWriteARowWithEveryField_UnderTheOwnersScope()
    {
        await Ledger().RecordAsync(Entry(_owner), CancellationToken.None);

        await using TradingCopilotDbContext reload = Context(_owner);
        AiUsageRecord row = await reload.AiUsage.SingleAsync();

        row.UserId.Should().Be(_owner);
        row.Feature.Should().Be(AiUsageFeature.Triage);
        row.Model.Should().Be("claude-haiku-4-5");
        row.Tier.Should().Be(LlmModelTier.Triage);
        row.Outcome.Should().Be(AiUsageOutcome.Succeeded);
        row.InputTokens.Should().Be(42);
        row.OutputTokens.Should().Be(9);
        row.EstimatedCostUsd.Should().Be(0.0006m);
        row.LatencyMs.Should().Be(1234);            // (long)Latency.TotalMilliseconds
        row.TraceId.Should().Be("trace-abc");
        row.OccurredAt.Should().Be(Now);            // the caller-supplied clock, verbatim
        row.TriggerFiringId.Should().Be(_firing);   // the firing this call served (gh#767) -- the per-suggestion cost key
    }

    [Fact]
    public async Task RecordAsync_ShouldWriteUnderTheEntryOwner_NotVisibleToAnotherOperator()
    {
        // R-20: the row is written under the entry owner's scope, so a STRANGER operator's context can never read it.
        await Ledger().RecordAsync(Entry(_owner), CancellationToken.None);

        Guid stranger = Guid.NewGuid();
        await using TradingCopilotDbContext theirs = Context(stranger);
        (await theirs.AiUsage.AnyAsync()).Should().BeFalse();

        // ...and it IS visible to its own owner -- proving the emptiness above is scoping, not a write that never landed.
        await using TradingCopilotDbContext mine = Context(_owner);
        (await mine.AiUsage.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_ShouldPersistAnEmbedShapedRow_UnderTheSystemOwnerScope()
    {
        // READINESS (gh#436): the EXISTING scoped ledger already accepts the settled EMBED shape, so the embed
        // consumer (gh#377) can reuse it verbatim -- Feature.Embed, Tier=null (embeddings have no model tier),
        // OutputTokens=0 (an embed returns a vector, not completion tokens), and Outcome.RateLimited (the 429
        // degrade-to-sparse maps here) -- stamped to the deployment SystemOwner sentinel, NOT an operator.
        // (DB-CHECK enforcement of this shape -- Tier null, zero cost/latency vs the >=0 CHECKs -- is Postgres-only
        // and belongs to the QA integration tier; the in-memory provider ignores CHECK constraints.)
        AiUsageEntry embed = new(
            SystemOwner.Id,
            new AiCallCost(
                AiUsageFeature.Embed, "embed-english-v3.0", Tier: null, AiUsageOutcome.RateLimited,
                InputTokens: 0, OutputTokens: 0, EstimatedCostUsd: 0m, Latency: TimeSpan.Zero),
            TraceId: "trace-embed",
            Now);

        await Ledger().RecordAsync(embed, CancellationToken.None);

        // It round-trips every embed-shaped field under the SystemOwner scope...
        await using TradingCopilotDbContext system = Context(SystemOwner.Id);
        AiUsageRecord row = await system.AiUsage.SingleAsync();
        row.UserId.Should().Be(SystemOwner.Id);
        row.Feature.Should().Be(AiUsageFeature.Embed);
        row.Model.Should().Be("embed-english-v3.0");
        row.Tier.Should().BeNull();
        row.Outcome.Should().Be(AiUsageOutcome.RateLimited);
        row.InputTokens.Should().Be(0);
        row.OutputTokens.Should().Be(0);
        row.TriggerFiringId.Should().BeNull(); // an embed serves no firing -- the correlation is null, not a fabricated link

        // ...and it is INVISIBLE to the operator's own scoped read -- deployment infra spend must not inflate the
        // operator's per-decision meter (gh#62, ADR-0008 "embeddings reported in Grafana, not surfaced to end users").
        await using TradingCopilotDbContext operatorContext = Context(_owner);
        (await operatorContext.AiUsage.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ShouldNotThrowAndLogAnError_WhenTheWriteFails()
    {
        // FAIL-OPEN: the AI result is already delivered, so a write fault (a CHECK violation, a DB blip) is logged and
        // swallowed -- it must never surface to the caller and roll back the call it was only recording.
        RecordingLogger<AiUsageLedger> logger = new();
        AiUsageLedger ledger = new TestableLedger(
            Options, logger, (_, _) => throw new InvalidOperationException("simulated DB fault"));

        Func<Task> act = () => ledger.RecordAsync(Entry(_owner), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Entries.Should().Contain(LogLevel.Error); // spend-blindness is a real fault -- logged, never silent
    }

    [Fact]
    public async Task RecordAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        // Our OWN shutdown is not a bookkeeping fault to swallow: a cancellation on the caller's token propagates, so a
        // graceful drain is never mistaken for a swallowed ledger blip.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        AiUsageLedger ledger = new TestableLedger(
            Options, NullLogger<AiUsageLedger>.Instance,
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        Func<Task> act = () => ledger.RecordAsync(Entry(_owner), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The real <see cref="AiUsageLedger"/> with its <c>WriteAsync</c> replaced by a supplied delegate — so a test can
    /// force a write fault (or a cancellation) through the PRODUCTION <c>RecordAsync</c> fail-open path without a DB.
    /// </summary>
    private sealed class TestableLedger(
        DbContextOptions<TradingCopilotDbContext> options,
        ILogger<AiUsageLedger> logger,
        Func<AiUsageEntry, CancellationToken, Task> write) : AiUsageLedger(options, logger)
    {
        protected override Task WriteAsync(AiUsageEntry entry, CancellationToken cancellationToken) =>
            write(entry, cancellationToken);
    }

    /// <summary>A minimal <see cref="ILogger{T}"/> that records the level of every log call, for asserting an error was logged.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(logLevel);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
