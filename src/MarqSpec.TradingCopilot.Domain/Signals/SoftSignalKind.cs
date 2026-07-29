namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// The kind of per-user feedback on a soft-signal / news item (gh#27, ADR-0014). A <b>refusable zero</b>:
/// <see cref="Unknown"/> is never a real kind — feedback must declare its intent — refused at construction and by a
/// DB check. One entity (<c>SoftSignalFeedback</c>) unifies all kinds; <b>star</b> and <b>mute</b> ship now, and the
/// 👍/👎 <b>sentiment</b> (direction) axis is deferred (R-2) and will extend this enum without reshaping the store.
/// </summary>
public enum SoftSignalKind
{
    /// <summary>Unset — never a real kind; refused at construction and by a DB check.</summary>
    Unknown = 0,

    /// <summary>The operator marked the item important — <b>raises</b> the salience of similar future items.</summary>
    Star = 1,

    /// <summary>The star's inverse — the operator down-weighted the item, <b>lowering</b> the salience of similar future items (never hiding them; ADR-0014 salience floor).</summary>
    Mute = 2,
}
