using System.Text.Json;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// Consumer-side coverage of the retention-gap signal (gh#228, gh#227): the two safety-critical event-log
/// consumers — <c>StopPromotionHost</c> and <c>ConditionalOrderHost</c> — must, when <c>ReadAfterAsync</c> reports
/// a gap, <b>log it high-severity and keep going</b>: neither swallow the signal (silent data loss) nor crash-loop
/// (the consumer dies). This runs the <b>live</b> hosts (via <see cref="RetentionConsumerTestPostgresFactory"/>,
/// which keeps the hosted services running and captures logs) and manufactures a real gap under a caught-up
/// consumer, then asserts the emergency log fired and the cursor advanced past it.
/// </summary>
/// <remarks>
/// The gap log is emitted inline in each host's poll loop — there is no directly-callable seam — so the only
/// faithful test is live-host + log-capture + poll. A genuine hole is manufactured by deleting the consumed window
/// and burning one sequence value so the next survivor is non-contiguous (the same shape a rolled-back append +
/// chunk drop leaves). Traces R-1 · ADR-0001 · Engineering guide §7, §9.
/// </remarks>
public class RetentionGapConsumerIntegrationTests : IClassFixture<RetentionConsumerTestPostgresFactory>
{
    private const string Contract = "projectx:CON.F.US.MES.U26";

    private readonly RetentionConsumerTestPostgresFactory _factory;

    public RetentionGapConsumerIntegrationTests(RetentionConsumerTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public Task StopPromotionConsumer_ShouldLogHighSeverityAndContinue_WhenGapSignalled() =>
        ManufactureGapAndAssertAsync(StopPromotionHost.ConsumerGroup);

    [Fact]
    public Task ConditionalOrderConsumer_ShouldLogHighSeverityAndContinue_WhenGapSignalled() =>
        ManufactureGapAndAssertAsync(ConditionalOrderHost.ConsumerGroup);

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private async Task ManufactureGapAndAssertAsync(string consumerGroup)
    {
        _factory.Logs.Clear();

        // 1. Append a quote and let the live consumer catch up (its in-memory cursor now sits on it).
        long consumed = (await AppendQuoteAsync()).Sequence;
        await WaitUntilAsync(
            async () => await GetCursorAsync(consumerGroup) >= consumed,
            $"the {consumerGroup} consumer to catch up to sequence {consumed}");

        // 2. Drop the consumed window and burn a sequence, so the survivor is non-contiguous — a genuine gap under
        //    a caught-up consumer whose cursor has fallen off the back of the log.
        await DeleteThroughAsync(consumed);
        await BurnSequenceAsync();
        long survivor = (await AppendQuoteAsync()).Sequence;

        // 3. The consumer must LOG the gap high-severity (Error) — not swallow it.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Any(entry =>
                entry.Level == LogLevel.Error && entry.Message.Contains($"Event-log retention gap on {consumerGroup}"))),
            $"the {consumerGroup} consumer to log the retention gap at Error severity");

        // 4. …and CONTINUE — it advances past the gap to the survivor rather than crash-looping or stalling.
        await WaitUntilAsync(
            async () => await GetCursorAsync(consumerGroup) >= survivor,
            $"the {consumerGroup} consumer to advance past the gap to sequence {survivor}");
    }

    private async Task<EventEnvelope> AppendQuoteAsync()
    {
        string payload = JsonSerializer.Serialize(new { contract = Contract, bid = 5_000m, ask = 5_000.25m });
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.AppendAsync(
            new EventDraft(QuoteIngestionService.QuoteEventType, "projectx", DateTimeOffset.UtcNow, payload), CancellationToken.None);
    }

    private async Task<long?> GetCursorAsync(string consumerGroup)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.GetCursorAsync(consumerGroup, CancellationToken.None);
    }

    private Task DeleteThroughAsync(long throughSequence) => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Events\" WHERE \"Sequence\" <= {throughSequence}"));

    // Burns one identity value so the next append is non-contiguous with the surviving window (the rolled-back /
    // dropped-chunk hole shape). ExecuteSqlRaw runs the nextval side effect even though it returns no rows.
    private Task BurnSequenceAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync("SELECT nextval(pg_get_serial_sequence('\"Events\"', 'Sequence'))"));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, int attempts = 150, int delayMs = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException($"Timed out waiting for {because}.");
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
