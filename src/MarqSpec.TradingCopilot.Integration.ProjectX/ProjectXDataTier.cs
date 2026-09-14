namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// Which market-data universe the gateway should answer from — ProjectX's <c>live</c> flag, which rides on
/// contract search and bar retrieval.
/// </summary>
/// <remarks>
/// This is not a nicety. Credentials are entitled to one tier or the other, and asking for the wrong one returns
/// an <b>empty result rather than an error</b>: practice credentials querying the live universe find no
/// contracts at all, which surfaces much later as "no contract matches ES". The tier is therefore explicit at
/// construction rather than defaulted. Venue-specific by nature, so it stays in the adapter (R-17).
/// </remarks>
public enum ProjectXDataTier
{
    /// <summary>The simulated universe, which practice and evaluation credentials are entitled to.</summary>
    Simulated,

    /// <summary>The live universe, for funded real-money credentials.</summary>
    Live,
}
