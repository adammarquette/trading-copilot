using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// The <b>live provider</b> host for the gh#1122 news suite: the real API pipeline over a throwaway Postgres
/// container, with the <b>real</b> <c>FinnhubNewsSource</c> and <c>TiingoNewsSource</c> at the
/// <c>INewsSource</c> seam — the two feeds the gh#360 pre-merge harness could only stand in for with
/// <see cref="StubNewsSource"/>, whose own remark placeholds "the (not-yet-built, gh#383) real providers".
/// gh#383 has since landed, so this host occupies that seam with the adapters themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is registered at the seam here, and that is the point.</b> The suite hands the host the provider
/// keys and lets <c>Program.cs</c>'s own conditional registration decide — so what is under test is the
/// <i>production construction path</i> (key present ⇒ client ⇒ adapter ⇒ seam), not a wiring the test invented.
/// A harness that re-registered <c>INewsSource</c> itself would be proving its own composition, and could pass
/// with the production registration broken or with a decorator the application adds silently dropped.
/// </para>
/// <para>
/// The production <c>NewsIngestionHost</c> is removed: it polls on a timer, and every case here drives
/// <c>NewsIngestionService.IngestAsync</c> directly so a pass is deterministic and cannot race the timer.
/// </para>
/// </remarks>
public class LiveNewsProviderFactory : PostgresApiFactory
{
    /// <summary>
    /// How far back a live poll looks, in minutes — three days, far wider than the 60-minute production default.
    /// </summary>
    /// <remarks>
    /// Deliberately not the production default, and the gap is itself a finding: measured against the live free
    /// tier (gh#1123), <b>zero</b> Finnhub articles fall inside a 60-minute window, because the feed publishes
    /// items that are already older than that. A suite running at the shipped default would therefore assert
    /// against an empty store and could only ever report "no news", telling us nothing about mapping, dedup or
    /// provenance. This window is wide enough that the pipeline has real payloads to be judged on; the latency
    /// itself is measured and reported by the data-quality case rather than smuggled into every other assertion.
    /// </remarks>
    public const int LookbackMinutes = 4320;

    /// <summary>The lookback the application actually ships with — reported against, never tested at.</summary>
    public const int ProductionDefaultLookbackMinutes = 60;

    /// <summary>The Finnhub key handed to the host; null leaves it unset, so the source is never registered.</summary>
    protected virtual string? FinnhubApiKey => LiveProviderConfig.FinnhubApiKey;

    /// <summary>The Tiingo key handed to the host; null leaves it unset, so the source is never registered.</summary>
    protected virtual string? TiingoApiKey => LiveProviderConfig.TiingoApiKey;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Straight into the application's own configuration keys, exactly as a deployment supplies them, so the
        // conditional registration in Program.cs is the thing being exercised.
        //
        // ALWAYS set, even to empty. Skipping the call for an absent key would leave the host reading whatever
        // `Finnhub__ApiKey` happens to be in the AMBIENT process environment — which, in a run of this very tier,
        // is a real key. The keyless variant below would then not be keyless, and its guard would fail for a
        // reason that has nothing to do with the property it guards. An empty value binds and registers nothing,
        // so every variant here gets exactly the credentials it declares and no others.
        builder.UseSetting("Finnhub:ApiKey", FinnhubApiKey ?? string.Empty);
        builder.UseSetting("Tiingo:ApiKey", TiingoApiKey ?? string.Empty);

        builder.UseSetting("News:LookbackMinutes", LookbackMinutes.ToString());
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Drop the timer-driven hosts so the only ingestion is the one a test triggers.
        foreach (ServiceDescriptor hosted in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                     .ToList())
        {
            services.Remove(hosted);
        }
    }
}

/// <summary>
/// The outage variant: a real Finnhub key and a deliberately invalid Tiingo one, so Tiingo is rejected by the
/// live API — a genuine provider refusal (<c>403 {"detail":"Invalid token."}</c>), not a stubbed throw — while
/// Finnhub still serves.
/// </summary>
/// <remarks>
/// An invalid key rather than the plan refusal Tiingo currently returns anyway (gh#1125), so the guard keeps
/// proving the same thing after that plan is fixed. The failure it models — one provider rejecting the request —
/// is the same either way, and this way the case does not quietly depend on a subscription staying unpaid.
/// </remarks>
public sealed class LiveNewsProviderTiingoOutageFactory : LiveNewsProviderFactory
{
    /// <summary>A syntactically plausible but unissued token, so the provider itself rejects it.</summary>
    protected override string? TiingoApiKey => "0000000000000000000000000000000000000000";
}

/// <summary>
/// The keyless variant: <b>no</b> provider keys at all. The host that must produce no news whatsoever — the
/// by-construction half of "credentials resolve from configuration only" (gh#1122).
/// </summary>
public sealed class KeylessNewsProviderFactory : LiveNewsProviderFactory
{
    /// <inheritdoc />
    protected override string? FinnhubApiKey => null;

    /// <inheritdoc />
    protected override string? TiingoApiKey => null;
}
