using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when <b>both</b> live news providers are configured — the real
/// Finnhub and Tiingo APIs, from <c>Finnhub__ApiKey</c> / <c>Tiingo__ApiKey</c> (gh#1122). Otherwise it sets
/// <see cref="FactAttribute.Skip"/>, so it never runs pre-merge and its absence is reported, not silently green.
/// </summary>
/// <remarks>
/// <b>Both</b> keys, deliberately, even for a case that reads as single-provider: every case in the suite is about
/// what happens when two feeds are fanned in — including the outage case, which asserts that the surviving
/// provider still lands news, and therefore needs that provider genuinely live. A gate satisfied by one key would
/// let the cross-source cases run half-blind and report a pass for a fan-in that never fanned in.
/// </remarks>
public sealed class LiveNewsProviderFactAttribute : FactAttribute
{
    /// <summary>Skips unless both provider keys are configured.</summary>
    public LiveNewsProviderFactAttribute()
    {
        if (!LiveProviderConfig.BothProvidersConfigured)
        {
            Skip = LiveProviderConfig.SkipReason();
        }
    }
}
