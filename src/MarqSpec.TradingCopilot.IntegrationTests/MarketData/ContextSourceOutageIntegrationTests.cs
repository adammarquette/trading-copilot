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
/// assignable to the executable-price seams at all. Its <b>own operations are exercised, not merely inspected</b>
/// (PR #711 review): <c>ResolveContractAsync</c> and <c>StreamContextTradesAsync</c> are both called on the
/// adapter resolved from the container, so the <c>Capabilities.Require(ContextTrades)</c> each opens with is on
/// the stack, and the stream call carries the runtime half of "refuses, never returns empty" — the provider is
/// unreachable for the whole run, so the seam must surface that failure rather than hand back an empty sequence.
/// </para>
/// <para>
/// <b>The residual, stated rather than faked</b> (gh#712). The one shape that cannot be reached from here is a
/// <c>FinnhubMarketDataSource</c> that does <i>not</i> grant <see cref="VenueCapability.ContextTrades"/>: the
/// grant is a compile-time constant on a sealed type, so removing <c>StreamContextTradesAsync</c>'s own
/// <c>Require</c> is unobservable through the real adapter, and the only alternative subject is a double whose
/// capability set — and whose refusal — the test itself would have written. gh#705's scoping note asked for this
/// against the real adapter precisely to avoid that, so it is carded (gh#712) rather than satisfied with a stub
/// asserting itself. What this suite does guard is the input to that <c>Require</c>: the grant is read off the
/// shipped adapter, so an adapter that started claiming <c>Quotes</c> — the refactor that would actually put a
/// context feed on the execution path — turns the matrix below red.
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
        // 0. SNAPSHOT EVERY CUMULATIVE COUNTER FIRST (PR #711 review). The transport's attempt count and the
        //    captured log both live on the class fixture, which the capability case below boots and shares — so an
        //    absolute "at least two" could be satisfied entirely by history, and a subscription that had since
        //    stalled would be indistinguishable from one still cycling. Every assertion in this test is therefore
        //    about growth AFTER this line: what the platform is doing now, not what it once did.
        int contextAttemptsAtStart = _factory.ContextTransport.SubscribeAttempts;
        int contextDropsAtStart = ContextDropsLogged();
        int quoteCyclesAtStart = QuoteSubscriptionCycles();

        //    Building the client boots the host and its background services — the context host, the quote host and
        //    the two execution watchers all start here, and nothing below can be observed before they do.
        using HttpClient client = _factory.CreateClient();

        // 1. THE OUTAGE IS REAL, AND ONGOING. Asserted before anything else: a platform whose context feed is
        //    healthy proves nothing about independence, and "the supervisor retried" distinguishes a live outage
        //    from a single failure that quietly ended the subscription for good.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.ContextTransport.SubscribeAttempts >= contextAttemptsAtStart + 2),
            "the context supervisor to have retried the downed provider twice SINCE this test began");

        //    A fresh drop log, not merely some drop log: the attempt counter increments inside the transport, and
        //    the supervisor writes this only once the failure has propagated all the way back up through the real
        //    adapter — so it is the wait, not the counter, that witnesses the full outage path.
        await WaitUntilAsync(
            () => Task.FromResult(ContextDropsLogged() > contextDropsAtStart),
            "the context subscription to report a FRESH drop — an outage that logged nothing would be the silent "
            + "never-ticks gap the seam exists to prevent");

        int contextAttemptsWithOutageProven = _factory.ContextTransport.SubscribeAttempts;

        // 2. THE APPLICATION IS STILL UP. This is the StopHost failure mode, stated directly: had the exception
        //    escaped ContextIngestionHost, there would be no web host left to answer.
        using HttpResponseMessage health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a cross-asset context outage must never stop the platform — the auto-flatten watchdog and the kill "
            + "switch ride the same host");

        // 3. THE TRADEABLE INGESTION HOST IS ALIVE — measured from the snapshot, never absolutely (PR #711 review).
        //    Its supervisor re-subscribes on every stream end, so two cycles logged AFTER this test began mean at
        //    least one complete end -> back off -> re-subscribe -> end round trip ran while context was failing.
        //    That is the quote path's own witness that it is still looping; a count inherited from an earlier case
        //    would have said only that it once did.
        await WaitUntilAsync(
            () => Task.FromResult(QuoteSubscriptionCycles() >= quoteCyclesAtStart + 2),
            "the ProjectX quote subscription to complete two FRESH cycles while the context source is down");

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

        // And the outage never stopped: the retries kept coming THROUGHOUT the tradeable work above, so none of it
        // happened in a window where context had quietly given up and the two feeds were no longer contending at
        // all. Measured against the count taken when the outage was first proven, not against a bare constant —
        // and WAITED FOR (gh#739), not sampled once. The supervisor's retry cadence is a free-running background
        // loop with no relationship to how long steps 2-5 happen to take, so a single point-in-time comparison can
        // land exactly on the boundary on a slow/busy runner even though the property itself — "still retrying" —
        // holds; that was gh#705's flake. The guard still fails on the defect it exists to catch: a supervisor
        // that has quietly stopped retrying will never produce another attempt, and this poll times out.
        await WaitUntilAsync(
            () => Task.FromResult(_factory.ContextTransport.SubscribeAttempts > contextAttemptsWithOutageProven),
            "the context supervisor to make at least one MORE resubscribe attempt beyond the count taken when the "
            + "outage was first proven — the outage is a live condition throughout the tradeable path's work, not "
            + "a blip that had ended before the quotes flowed");
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

        // ---- The source's OWN operations, exercised rather than inspected (PR #711 review) ---------------------
        //
        // Everything above reads a capability value off the adapter and drives VenueCapabilities.Require directly,
        // which is a statement about the value, not about the adapter's behaviour. These two calls run the shipped
        // methods themselves — each opens with Capabilities.Require(ContextTrades) — so the enforcement path is on
        // the stack rather than reasoned about, and the seam is asked to do real work.
        ResolvedContract resolved = await source.ResolveContractAsync(
            InstrumentId.Parse(ContextOutageTestPostgresFactory.ContextSymbol), CancellationToken.None);

        resolved.Contract.ToString().Should().Be(
            $"finnhub:{ContextOutageTestPostgresFactory.ContextSymbol}",
            "the context source mints handles under its OWN venue — a finnhub SPY and a projectx contract must never "
            + "compare equal however alike their keys look, which is what keeps a context print off the execution path");
        resolved.Instrument.Symbol.Should().Be(
            ContextOutageTestPostgresFactory.ContextSymbol,
            "a handle is returned PAIRED with the instrument it was resolved for, so a caller can never size one "
            + "instrument and subscribe another");

        // The live half of "refuses, never returns empty". The provider behind this adapter is unreachable for the
        // whole run, and asking it to stream must SURFACE that: an adapter that swallowed the downed subscribe and
        // handed back an empty sequence would be exactly the silent never-ticks gap R-17's loud refusal exists to
        // prevent (gh#496) — a caller would sit forever on a feed that had already failed. This is the assertion
        // that goes red for it, and it can only pass by running StreamContextTradesAsync for real.
        List<ContextTrade> streamed = [];
        Func<Task> streamWhileTheProviderIsDown = async () =>
        {
            await foreach (ContextTrade print in source.StreamContextTradesAsync(
                resolved.Contract, CancellationToken.None))
            {
                streamed.Add(print);
            }
        };

        await streamWhileTheProviderIsDown.Should().ThrowAsync<IOException>(
            "asking the REAL adapter to stream while its provider is unreachable must fail loudly — an empty "
            + "sequence is the one answer a context seam may never give");

        streamed.Should().BeEmpty(
            "the downed transport feeds nothing, so the refusal above is the whole of the adapter's answer");

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

    /// <summary>
    /// How many drops the <b>context</b> supervisor has reported. Cumulative across the class fixture, so callers
    /// compare growth, never an absolute — the fixture's outage is a standing condition, and an entry inherited
    /// from an earlier case would say nothing about whether the supervisor is still retrying now.
    /// </summary>
    private int ContextDropsLogged() => _factory.Logs.Entries.Count(entry =>
        entry.Message.Contains("Context stream for", StringComparison.Ordinal)
        && entry.Message.Contains("dropped", StringComparison.Ordinal));

    /// <summary>
    /// How many subscription cycles the <b>tradeable</b> quote supervisor has completed — exactly one log per
    /// stream end, written immediately before it backs off and re-subscribes. Cumulative for the same reason.
    /// </summary>
    private int QuoteSubscriptionCycles() => _factory.Logs.Entries.Count(entry =>
        entry.Message.Contains("Quote stream for", StringComparison.Ordinal));

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
