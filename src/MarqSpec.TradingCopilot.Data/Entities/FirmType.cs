namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// What kind of account provider a <see cref="Firm"/> is. A <b>prop firm</b> runs staged programmes
/// (evaluation → funded); a <b>brokerage</b> does not — its accounts are simply live or paper. The distinction
/// matters because stage conventions (gh#60) apply to prop firms only (gh#76).
/// </summary>
public enum FirmType
{
    /// <summary>A proprietary trading firm with staged programmes (Topstep, Apex Trader Funding, …).</summary>
    PropFirm,

    /// <summary>A brokerage — no evaluation/funded stages; accounts are live or paper.</summary>
    Brokerage,
}
