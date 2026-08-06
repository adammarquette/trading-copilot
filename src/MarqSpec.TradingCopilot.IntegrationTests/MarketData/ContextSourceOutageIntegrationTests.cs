using System.Net;
using System.Runtime.CompilerServices;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Finnhub;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// The second half of gh#705, of gh#608 — a <b>context outage with the real adapter in the loop</b> (case 3) and
/// <b>capability refusal at the seam</b> (case 5). Traces gh#496, gh#495, gh#411 · R-1, R-17 · ADR-0001 · gh#557
/// (the live-host fixture shape) · gh#304 (the <c>ContextIngestion__Symbols__0..7</c> allowlist shape).
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture is the point.</b> <see cref="ContextOutageTestPostgresFactory"/> configures context ingestion
/// <i>and</i> credentials it, so <c>FinnhubMarketDataSource</c> is genuinely registered behind
/// <c>IContextMarketDataSource</c> exactly as a deployment registers it — and then takes its <b>websocket
/// transport</b> down. The shipped adapter, the shipped <c>ContextSubscriptionSupervisor</c> and the shipped
/// <c>ContextIngestionHost</c> all run for real; only the socket is unreachable.
/// </para>
/// <para>
/// <b>Case 3 is gh#496's sharpest acceptance criterion</b>, and the mechanism that would break it is in the
/// framework, not the feature: .NET's default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so an
/// exception escaping <c>ContextIngestionHost.ExecuteAsync</c> stops the <i>entire application</i> — the tradeable
/// quote feed, the auto-flatten watchdog and the kill switch with it — because an optional colour feed went down.
/// The guard is therefore not "context failed gracefully" but "the tradeable path demonstrably kept working while
/// context was failing", and the suite proves the outage is real and ongoing <b>first</b>, so a green cannot mean
/// "nothing was ever broken".
/// </para>
/// <para>
/// <b>Case 5, and what is honestly testable of it.</b> gh#608's scoping note already records that half of case 5 —
/// asking a context source for execution-shaped work — is not expressible: <c>IContextMarketDataSource</c> exposes
/// only <c>ResolveContractAsync</c> and <c>StreamContextTradesAsync</c>, because the separation lives in the type
/// system (gh#496's design). What remains is the runtime half, and it is asserted here against the <b>real</b>
/// adapter resolved from the running host's container rather than a stub whose capability set is whatever a test
/// says it is: the source refuses every capability it does not grant with
/// <see cref="VenueCapabilityNotSupportedException"/> — an explicit failure, never an empty result — and it is not
/// assignable to the executable-price seams at all. The one shape that cannot be reached from here is a
/// <c>FinnhubMarketDataSource</c> that does <i>not</i> grant <see cref="VenueCapability.ContextTrades"/>: its grant
/// is a constant, so <c>StreamContextTradesAsync</c>'s own <c>Require</c> can only be witnessed on a source double,
/// which would be a stub asserting itself. That is noted rather than faked.
/// </para>
/// </remarks>
public class ContextSourceOutageIntegrationTests : IClassFixture<ContextOutageTestPostgresFactory>
{
    /// <summary>This suite's own contract key, so no other suite's rows can answer for its assertions.</summary>
    private const string ContractKey = "CTX705-OUTAGE";

    private readonly ContextOutageTestPostgresFactory _factory;

    public ContextSourceOutageIntegrationTests(ContextOutageTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // gh#608 case 3 — a context outage does not disturb tradeable ingestion.
    // =============================================================================================================

    [Fact]
    public async Task TradeableIngestion_ShouldKeepRunning_WhileTheContextSourceIsInOutage()
    {
        // 0. Building the client boots the host and its background services — the context host, the quote host and
        //    the two execution watchers all start here, and nothing below can be observed before they do.
        using HttpClient client = _factory.CreateClient();

        // 1. THE OUTAGE IS REAL, AND ONGOING. Asserted before anything else: a platform whose context feed is
        //    healthy proves nothing about independence, and "the supervisor retried" distinguishes a live outage
        //    from a single failure that quietly ended the subscription for good.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.ContextTransport.SubscribeAttempts >= 2),
            "the context supervisor to have retried the downed provider at least twice");

        _factory.Logs.Entries.Should().Contain(
            entry => entry.Message.Contains("Context stream for", StringComparison.Ordinal)
                && entry.Message.Contains("dropped", StringComparison.Ordinal),
            "the context subscription must report its drop — an outage that logged nothing would be the silent "
            + "never-ticks gap the seam exists to prevent");

        // 2. THE APPLICATION IS STILL UP. This is the StopHost failure mode, stated directly: had the exception
        //    escaped ContextIngestionHost, there would be no web host left to answer.
        using HttpResponseMessage health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a cross-asset context outage must never stop the platform — the auto-flatten watchdog and the kill "
            + "switch ride the same host");

        // 3. THE TRADEABLE INGESTION HOST IS ALIVE. Its supervisor re-subscribes on every stream end, so a second
        //    cycle logged while context is failing is the quote path's own witness that it is still looping.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Count(entry =>
                entry.Message.Contains("Quote stream for", StringComparison.Ordinal)) >= 2),
            "the ProjectX quote subscription to keep cycling while the context source is down");

        // 4. QUOTES STILL LAND — through the SHIPPED producer, into the real event log.
        int appended = await IngestQuotesAsync(bid: 5_299.75m, ask: 5_300m);
        appended.Should().Be(1, "the tradeable producer is unaffected by the context provider's outage");

        long sequence = await LatestQuoteSequenceAsync();

        // 5. …AND THE CURSORS ADVANCE. Both safety-critical consumers read past the new quote, which is the only
        //    evidence that separates "the host object still exists" from "the consumer is running".
        await WaitUntilAsync(
            async () => await CursorAsync(StopPromotionHost.ConsumerGroup) >= sequence,
            $"the stop-promotion consumer's cursor to advance past sequence {sequence}");
        await WaitUntilAsync(
            async () => await CursorAsync(ConditionalOrderHost.ConsumerGroup) >= sequence,
            $"the conditional-order consumer's cursor to advance past sequence {sequence}");

        // And the outage never stopped: the retries kept coming throughout, so none of the above happened in a
        // window where context had quietly given up and the two feeds were no longer contending at all.
        _factory.ContextTransport.SubscribeAttempts.Should().BeGreaterThan(
            2, "the context supervisor keeps retrying for the whole run — the outage is a live condition, not a blip");
    }

    // =============================================================================================================
    // gh#608 case 5 — capability refusal at the seam.
    // =============================================================================================================

    [Fact]
    public async Task ContextSource_ShouldRefuseWorkItDoesNotGrant_RatherThanReturningEmpty()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IContextMarketDataSource? registered = scope.ServiceProvider.GetService<IContextMarketDataSource>();

        // The registration itself is part of the case: with a key configured, the deployment wires the REAL
        // adapter. A stub here would make every assertion below a statement about the stub.
        registered.Should().BeOfType<FinnhubMarketDataSource>(
            "a credentialed deployment registers the shipped Finnhub adapter behind the context seam (gh#496)");

        IContextMarketDataSource source = registered!;
        source.Id.ToString().Should().Be("finnhub");

        VenueCapabilities capabilities = source.Capabilities;
        capabilities.Supports(VenueCapability.ContextTrades).Should().BeTrue(
            "the source must genuinely grant the one capability it exists for — otherwise the refusals below would "
            + "pass on a source that grants nothing at all");

        // The refusal is an EXCEPTION, not an empty result. R-17's whole point: a caller learns at the seam that a
        // capability is absent, instead of discovering it as a silently empty series mid-execution. Quotes is the
        // one that matters most — it is the executable-price capability the stop-promotion and conditional-firing
        // watchers act on, and a context feed with no book could only satisfy it by inventing a spread.
        foreach (VenueCapability ungranted in Ungranted())
        {
            capabilities.Supports(ungranted).Should().BeFalse(
                $"a data-only context source grants nothing but ContextTrades — {ungranted} is not its to give");

            Action require = () => capabilities.Require(ungranted);

            require.Should().Throw<VenueCapabilityNotSupportedException>(
                    $"asking the context source for {ungranted} must fail loudly, never return empty")
                .Which.MissingCapability.Should().Be(
                    ungranted, "the refusal names the capability that was missing, so the caller can act on it");
        }

        // The compile-time half of case 5, witnessed at runtime so it can actually go red. A "unified market data"
        // refactor that made the context source implement an executable-price seam would hand it to the execution
        // path directly — the failure the type split exists to make impossible (gh#496's operator decision).
        source.Should().NotBeAssignableTo<IMarketDataSource>(
            "a context source must never satisfy the executable-price seam — it publishes prints, not a book");
        source.Should().NotBeAssignableTo<ITradingVenue>(
            "a data-only context source holds no account and executes nothing");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    /// <summary>Every capability the context seam must refuse — the whole matrix bar the one it grants.</summary>
    private static IEnumerable<VenueCapability> Ungranted() => Enum
        .GetValues<VenueCapability>()
        .Where(capability => capability is not (VenueCapability.None or VenueCapability.ContextTrades));

    /// <summary>Runs the shipped quote producer over a feed-only market-data source — the tradeable path's producer.</summary>
    private async Task<int> IngestQuotesAsync(decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        QuoteIngestionService ingestion = scope.ServiceProvider.GetRequiredService<QuoteIngestionService>();

        return await ingestion.IngestQuotesAsync(
            new StubQuoteSource(new Quote(DateTimeOffset.UtcNow, new Price(bid), new Price(ask), null, null)),
            VenueContractId.Create(VenueId.Parse("projectx"), ContractKey),
            CancellationToken.None);
    }

    private async Task<long> LatestQuoteSequenceAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        // Payload is `jsonb`, so a `.Contains` predicate would translate to a string LIKE Postgres refuses on that
        // column type: the type filter runs in SQL and the contract filter in memory.
        List<QuoteRow> quotes = await database.Events
            .Where(row => row.Type == QuoteIngestionService.QuoteEventType)
            .OrderBy(row => row.Sequence)
            .Select(row => new QuoteRow(row.Sequence, row.Payload))
            .ToListAsync();

        List<QuoteRow> mine = [.. quotes.Where(row => row.Payload.Contains(ContractKey, StringComparison.Ordinal))];
        mine.Should().NotBeEmpty($"the quote on {ContractKey} must have reached the real event log");

        return mine.Max(row => row.Sequence);
    }

    private async Task<long?> CursorAsync(string consumerGroup)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.GetCursorAsync(consumerGroup, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string because, int attempts = 200, int delayMs = 250)
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

    private sealed record QuoteRow(long Sequence, string Payload);

    /// <summary>
    /// A market-data source that streams exactly the quotes it is handed. It feeds a bid and an ask and answers
    /// nothing about what the system did with them, so every assertion is read back from Postgres.
    /// </summary>
    private sealed class StubQuoteSource : IMarketDataSource
    {
        private readonly Quote[] _quotes;

        public StubQuoteSource(params Quote[] quotes)
        {
            _quotes = quotes;
        }

        public VenueId Id { get; } = VenueId.Parse("projectx");

        public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.Quotes);

        public int AdapterLogicVersion => 1;

        public Task<ResolvedContract> ResolveContractAsync(
            InstrumentId instrument, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This case drives the stream directly with an already-resolved contract.");

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            VenueContractId contract,
            DateTimeOffset from,
            DateTimeOffset to,
            TimeSpan barSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The tradeable-continuity case streams live quotes; it asks for no history.");

        public async IAsyncEnumerable<Quote> StreamQuotesAsync(
            VenueContractId contract,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (Quote quote in _quotes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return quote;
                await Task.Yield();
            }
        }
    }
}
