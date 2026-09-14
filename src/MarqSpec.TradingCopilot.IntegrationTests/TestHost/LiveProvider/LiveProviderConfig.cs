namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;

/// <summary>
/// Binds the <b>live provider</b> test surface from environment variables (gh#1122) — <b>never</b> from source.
/// The tier sits between pre-merge and staging: it makes real outbound calls to Finnhub and Tiingo, so it cannot
/// run on every PR, but it needs <b>no deployed application</b> — the store is the same throwaway Postgres
/// container the pre-merge tier uses, and the providers are data-only (R-17), holding no account and executing
/// nothing. When the keys are absent the matching <c>LiveNewsProviderFact</c> skips by construction, so pre-merge
/// CI stays green and the suite runs only where credentials actually exist.
/// </summary>
/// <remarks>
/// <para>
/// The names are the application's own configuration keys (<c>Finnhub__ApiKey</c> / <c>Tiingo__ApiKey</c>,
/// <c>.env.example</c>), not a test-only alias: the suite hands them straight to the production host so the
/// registration path under test is the shipped one.
/// </para>
/// <para>
/// <b>Both spellings are probed, and that is load-bearing.</b> Environment variable lookup is case-insensitive on
/// Windows and case-<i>sensitive</i> on Linux, while the repository secrets are spelled <c>FINNHUB__APIKEY</c> /
/// <c>TIINGO__APIKEY</c>. Probing the exact name alone would bind locally and silently miss on the ubuntu runner —
/// reporting a skip that reads as "no credentials" when the credentials are in fact right there. A tier whose
/// whole value is running for real must not have a platform-shaped way to quietly not run.
/// </para>
/// </remarks>
internal static class LiveProviderConfig
{
    /// <summary>The Finnhub API key, or null when unset.</summary>
    public static string? FinnhubApiKey => Env("Finnhub__ApiKey", "FINNHUB__APIKEY");

    /// <summary>The Tiingo API key, or null when unset.</summary>
    public static string? TiingoApiKey => Env("Tiingo__ApiKey", "TIINGO__APIKEY");

    /// <summary>Whether both provider keys are present — the cross-source cases need two live feeds, not one.</summary>
    public static bool BothProvidersConfigured =>
        !string.IsNullOrWhiteSpace(FinnhubApiKey) && !string.IsNullOrWhiteSpace(TiingoApiKey);

    /// <summary>The skip reason a <c>LiveNewsProviderFact</c> reports when its credentials are not configured.</summary>
    public static string SkipReason() =>
        "Live news providers not configured (needs Finnhub__ApiKey and Tiingo__ApiKey) — live-provider tier only, "
        + "skipped pre-merge. Run '.github/workflows/live-provider-gates.yml' (workflow_dispatch) to execute it.";

    private static string? Env(params string[] names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
