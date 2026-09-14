namespace MarqSpec.TradingCopilot.Domain.Relevance;

/// <summary>
/// A topic's reach (gh#359). A <b>refusable zero</b>: <see cref="Unknown"/> never matches — a topic must declare
/// whether it is instrument-scoped or global, so an unset scope can't silently attach news to an instrument.
/// Refused at construction and by a DB check.
/// </summary>
public enum TopicScope
{
    /// <summary>Unset — never a real scope; refused at construction and by a DB check.</summary>
    Unknown = 0,

    /// <summary>Scoped to one instrument — a keyword match also marks that instrument relevant.</summary>
    Instrument = 1,

    /// <summary>Deployment-wide — a keyword match surfaces the item globally, attached to no instrument.</summary>
    Global = 2,
}
