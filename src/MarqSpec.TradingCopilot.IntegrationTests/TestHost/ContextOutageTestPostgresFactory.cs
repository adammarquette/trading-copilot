using System.Runtime.CompilerServices;
using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Api.MarketData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The live-host fixture for a cross-asset <b>context outage</b> with the real Finnhub adapter in the loop
/// (gh#705, of gh#608 / gh#496): context ingestion is configured <i>and</i> credentialed, so
/// <c>FinnhubMarketDataSource</c> is genuinely registered behind <c>IContextMarketDataSource</c> — and its
/// websocket transport is down.
/// </summary>
/// <remarks>
/// <para>
/// <b>The double sits at the transport, not at the source.</b> Replacing <c>IContextMarketDataSource</c> wholesale
/// would take the adapter under test out of the picture; replacing only <see cref="IFinnhubQuoteStream"/> leaves
/// the shipped adapter, the shipped supervisor and the shipped host all running, with just the socket unreachable.
/// It is the venue seam's transport for a data-only venue and it is adversarial in the QA-contract sense: it
/// computes nothing and answers nothing — it only fails, and counts how often it was asked.
/// </para>
/// <para>
/// <b>The tradeable feed is configured too</b> (<c>Ingestion:Symbols</c>), so <c>MarketDataIngestionHost</c> is
/// live alongside the context host. gh#496's acceptance criterion is about the two <i>not</i> interfering, and a
/// tradeable host that was never started could not witness that. Both reconnect delays are shortened to a second
/// so a suite sees several supervised retries without a long wait.
/// </para>
/// <para>
/// <b>No secret is in source.</b> The Finnhub token below is a fabricated string that never leaves the process —
/// the transport it would authenticate is replaced above, so nothing here can reach Finnhub. It exists only
/// because <c>Program.cs</c> registers the context source <i>only</i> when a key is configured, and this fixture's
/// whole point is that the source IS registered (the same pattern as
/// <see cref="PostgresApiFactory.OperatorPassword"/>).
/// </para>
/// </remarks>
public sealed class ContextOutageTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The configured context symbol — an equity, deliberately not a futures contract.</summary>
    public const string ContextSymbol = "SPY";

    /// <summary>The configured tradeable symbol, so the ProjectX quote host runs alongside the context host.</summary>
    public const string TradeableSymbol = "MES";

    /// <summary>A fabricated token, not a secret: the transport it would authenticate is replaced (see remarks).</summary>
    private const string FabricatedFinnhubToken = "integration-test-finnhub-token-not-a-real-key";

    /// <summary>The captured log stream — the only surface a background host's outage handling reports through.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    /// <summary>The downed websocket transport behind the real Finnhub adapter.</summary>
    public OutageFinnhubQuoteStream ContextTransport { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Finnhub:ApiKey", FabricatedFinnhubToken);

        // gh#304's allowlist shape (ContextIngestion__Symbols__0 under compose).
        builder.UseSetting($"{ContextIngestionOptions.SectionName}:Symbols:0", ContextSymbol);
        builder.UseSetting($"{ContextIngestionOptions.SectionName}:ReconnectDelaySeconds", "1");

        builder.UseSetting($"{IngestionOptions.SectionName}:Symbols:0", TradeableSymbol);
        builder.UseSetting($"{IngestionOptions.SectionName}:ReconnectDelaySeconds", "1");

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor transport in services
            .Where(descriptor => descriptor.ServiceType == typeof(IFinnhubQuoteStream))
            .ToList())
        {
            services.Remove(transport);
        }

        services.AddScoped<IFinnhubQuoteStream>(_ => ContextTransport);
    }
}

/// <summary>
/// A Finnhub websocket transport that is <b>down</b> (gh#705): every subscribe and every read faults, the shape an
/// unreachable provider leaves behind. It feeds no data and decides nothing — it only records how often the
/// adapter above it tried, so a suite can tell "the outage is live and being retried" from "nothing was ever
/// configured", which is the difference between a guard and a vacuous pass.
/// </summary>
public sealed class OutageFinnhubQuoteStream : IFinnhubQuoteStream
{
    private int _subscribeAttempts;

    /// <summary>Whether the transport is down. Settable so a case could restore it; down is the default.</summary>
    public bool IsDown { get; set; } = true;

    /// <summary>How many times the adapter has tried to subscribe a symbol on this transport.</summary>
    public int SubscribeAttempts => Volatile.Read(ref _subscribeAttempts);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Subscribed => [];

    /// <inheritdoc />
    public Task SubscribeAsync(string symbol, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _subscribeAttempts);

        return IsDown ? throw Outage() : Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<FinnhubTrade> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (IsDown)
        {
            throw Outage();
        }

        yield break;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static IOException Outage() =>
        new("The Finnhub websocket is unreachable (integration-test outage, gh#705).");
}
