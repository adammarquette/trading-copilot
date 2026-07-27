namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Binds the <b>post-merge staging</b> test surface from environment variables (QA contract §1) — <b>never</b> from
/// source. Every value is a CI secret; when a capability's group is unset the matching <c>Staging*Fact</c> attribute
/// <b>skips by construction</b>, so pre-merge CI (which has no staging environment) stays green and these suites run
/// only where the deployment and its credentials actually exist.
/// </summary>
/// <remarks>
/// Three independent capability groups, so a run configured for one need not configure the others:
/// <list type="bullet">
/// <item><b>API</b> — the deployed staging API every staging suite authenticates against.</item>
/// <item><b>Venue execution</b> — a <b>dedicated ProjectX practice account</b> reserved for CI (gh#269, gh#293).
/// PRACTICE ONLY outside production (R-14): a live real-money account is never wired into staging.</item>
/// <item><b>Observability</b> — the live Prometheus / Loki / Tempo backends the telemetry SLIs are asserted
/// against (gh#234).</item>
/// </list>
/// </remarks>
internal static class StagingConfig
{
    // --- Deployed staging API (every staging suite) ---
    public static string? ApiBaseUrl => Env("STAGING_API_BASE_URL");
    public static string? OperatorEmail => Env("STAGING_OPERATOR_EMAIL");
    public static string? OperatorPassword => Env("STAGING_OPERATOR_PASSWORD");
    public static bool ApiConfigured => AllSet(ApiBaseUrl, OperatorEmail, OperatorPassword);

    // --- ProjectX PRACTICE account reserved for the CI execution gates (gh#269, gh#293) ---
    public static string? VenueCredentialKey => Env("STAGING_PROJECTX_CREDENTIAL_KEY");
    public static string? PracticeAccountKey => Env("STAGING_PROJECTX_PRACTICE_ACCOUNT");
    public static string? ExecutionInstrument => Env("STAGING_EXECUTION_INSTRUMENT");
    public static bool VenueExecutionConfigured =>
        ApiConfigured && AllSet(VenueCredentialKey, PracticeAccountKey, ExecutionInstrument);

    // --- Observability backends (gh#234 staging portion) ---
    public static string? PrometheusBaseUrl => Env("STAGING_PROMETHEUS_URL");
    public static string? LokiBaseUrl => Env("STAGING_LOKI_URL");
    public static string? TempoBaseUrl => Env("STAGING_TEMPO_URL");
    public static bool ObservabilityConfigured =>
        ApiConfigured && AllSet(PrometheusBaseUrl, LokiBaseUrl, TempoBaseUrl);

    /// <summary>The skip reason a <c>Staging*Fact</c> reports when its capability group is not configured.</summary>
    public static string SkipReason(string capability, string envVars) =>
        $"Staging {capability} not configured (needs {envVars}) — post-merge staging tier only, skipped pre-merge.";

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static bool AllSet(params string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));
}
