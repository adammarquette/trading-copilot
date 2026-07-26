namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// What the clean-historical backfill fetches, and how often (gh#302, R-1). Bound from the <c>Backfill</c>
/// config section.
/// </summary>
/// <remarks>
/// <b>Deliberately separate from <c>Ingestion</c>.</b> The live stream and the historical series are different
/// paths that R-1 keeps apart, and an operator may reasonably want one without the other — history for
/// journaling and replay without a live subscription, or a live feed on an instrument they are not archiving.
/// Sharing one symbol list would have quietly coupled them.
/// </remarks>
public sealed class BarBackfillOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Backfill";

    /// <summary>
    /// The instruments to archive. Empty — the default — leaves the backfill idle: unconfigured means idle,
    /// never "fetch everything".
    /// </summary>
    public string[] Instruments { get; init; } = [];

    /// <summary>The bar sizes to archive, in minutes. Each is an independent series.</summary>
    public int[] ResolutionMinutes { get; init; } = [1];

    /// <summary>
    /// How often the poller runs. Engineering §8 pins the default: <i>"The REST poll interval is
    /// user-configurable (default 60s, an env / Options setting for now)"</i>.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>
    /// How far back each poll re-fetches. Deliberately wider than the poll interval so a pass covers the
    /// previous one's window: that is what lets a venue's <b>revision</b> of an already-stored bar be picked up,
    /// and what makes a missed pass self-healing rather than a permanent hole.
    /// </summary>
    public int LookbackMinutes { get; init; } = 120;

    /// <summary>Whether anything is configured to archive.</summary>
    public bool IsConfigured => Instruments.Length > 0 && ResolutionMinutes.Length > 0;
}
