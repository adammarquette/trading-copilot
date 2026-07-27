using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when the staging <b>venue-execution</b> surface is configured — the
/// deployed API plus a reserved ProjectX practice account (gh#269, gh#293). Otherwise it sets <see cref="FactAttribute.Skip"/>,
/// so it never runs pre-merge and its absence is reported, not silently green.
/// </summary>
public sealed class StagingVenueFactAttribute : FactAttribute
{
    /// <summary>Skips unless the venue-execution env group is set.</summary>
    public StagingVenueFactAttribute()
    {
        if (!StagingConfig.VenueExecutionConfigured)
        {
            Skip = StagingConfig.SkipReason(
                "venue execution",
                "STAGING_API_BASE_URL, STAGING_OPERATOR_EMAIL/PASSWORD, STAGING_PROJECTX_CREDENTIAL_KEY, "
                + "STAGING_PROJECTX_PRACTICE_ACCOUNT, STAGING_EXECUTION_INSTRUMENT");
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when the staging <b>observability</b> backends are configured — the
/// deployed API plus live Prometheus / Loki / Tempo endpoints (gh#234). Otherwise it skips.
/// </summary>
public sealed class StagingObservabilityFactAttribute : FactAttribute
{
    /// <summary>Skips unless the observability env group is set.</summary>
    public StagingObservabilityFactAttribute()
    {
        if (!StagingConfig.ObservabilityConfigured)
        {
            Skip = StagingConfig.SkipReason(
                "observability",
                "STAGING_API_BASE_URL, STAGING_OPERATOR_EMAIL/PASSWORD, STAGING_PROMETHEUS_URL, "
                + "STAGING_LOKI_URL, STAGING_TEMPO_URL");
        }
    }
}
