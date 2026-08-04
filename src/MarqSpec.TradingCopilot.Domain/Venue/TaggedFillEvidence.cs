namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// What the venue could tell us about whether an order stamped with a given <c>customTag</c> ever <b>filled</b>
/// (gh#631) — the fill-level counterpart to the resting-order read, which only ever sees orders still working.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four-valued on purpose.</b> "It filled", "it demonstrably did not", "I could not ask" and "this venue cannot
/// answer that question" are four different answers, and collapsing any pair of them is how a stranded row gets
/// resolved the wrong way. In particular <see cref="NoFillFound"/> is <b>not</b> the absence of an answer — it is a
/// positive report from a venue that was reachable and does carry fill history.
/// </para>
/// </remarks>
public enum TaggedFillStatus
{
    /// <summary>Never a valid outcome — an unset value, refused rather than interpreted.</summary>
    Unknown = 0,

    /// <summary>
    /// The venue reports an order under this tag that <b>filled</b> (in whole or in part). The only value that
    /// carries a size and price.
    /// </summary>
    Filled = 1,

    /// <summary>
    /// The venue was reachable, carries fill history, and reports <b>no fill</b> under this tag. A positive report,
    /// not a shrug — but see the caller-side rule in <see cref="TaggedFillEvidence"/>: it still authorises nothing.
    /// </summary>
    NoFillFound = 2,

    /// <summary>
    /// The venue <i>can</i> answer, but this attempt failed — a throw, a timeout, an unreadable response. <b>Never
    /// read as "did not fill".</b>
    /// </summary>
    Unavailable = 3,

    /// <summary>
    /// This venue has no fill-history capability at all (R-17). Distinct from <see cref="Unavailable"/> so that a
    /// venue which never had the capability does not permanently strand every reconcile.
    /// </summary>
    Unsupported = 4,
}

/// <summary>
/// The venue's answer to "did the order stamped <paramref name="CustomTag"/> ever fill?" (gh#631).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a veto, never an authorisation — the single most important property of this type.</b> A caller may
/// use <see cref="TaggedFillStatus.Filled"/> to <i>stop</i> an action it would otherwise have taken (releasing a
/// stranded row, re-arming a one-shot conditional). It must <b>never</b> use any other value to <i>start</i> an
/// action it would not otherwise have taken.
/// </para>
/// <para>
/// The reason is that <see cref="TaggedFillStatus.NoFillFound"/> is a <b>negative existence claim over an external
/// search index</b>, and those are not trustworthy enough to authorise a re-transmission: a venue that answers "no
/// rows" when the truth is "not yet indexed", "indexed without the tag echoed on the terminal record", or "silently
/// truncated" is indistinguishable from one that is genuinely reporting no fill. Used as a veto, that uncertainty
/// can only ever cost us a missed optimisation — the caller falls back to the behaviour it already had. Used as an
/// authorisation, the same uncertainty places a second live order against a real account.
/// </para>
/// <para>
/// <b>A partial fill is a fill.</b> Any executed quantity means the order reached the market and did something, so
/// it vetoes exactly as a complete fill does.
/// </para>
/// </remarks>
/// <param name="Status">Which of the four answers this is.</param>
/// <param name="CustomTag">The tag that was asked about.</param>
/// <param name="FilledSize">
/// The executed quantity — meaningful only when <see cref="Status"/> is <see cref="TaggedFillStatus.Filled"/>, and
/// then always greater than zero.
/// </param>
/// <param name="FilledPrice">The average fill price when the venue reports one. <c>decimal</c> — a price is money.</param>
/// <param name="VenueOrderKey">The venue's own identifier for the filled order, when it reports one.</param>
public sealed record TaggedFillEvidence(
    TaggedFillStatus Status,
    string CustomTag,
    decimal FilledSize = 0m,
    decimal? FilledPrice = null,
    string? VenueOrderKey = null)
{
    /// <summary>
    /// Whether this evidence <b>vetoes</b> a release or re-arm — true only for a positively reported fill.
    /// </summary>
    /// <remarks>
    /// Deliberately the only convenience predicate on this type. There is no <c>DidNotFill</c> counterpart, because
    /// offering one would invite exactly the authorising use the type exists to prevent.
    /// </remarks>
    public bool VetoesRelease => Status == TaggedFillStatus.Filled;

    /// <summary>The venue reports a fill under <paramref name="customTag"/>.</summary>
    /// <param name="customTag">The tag that filled.</param>
    /// <param name="filledSize">The executed quantity; must be greater than zero.</param>
    /// <param name="filledPrice">The average fill price, when reported.</param>
    /// <param name="venueOrderKey">The venue's identifier for the order, when reported.</param>
    /// <returns>Evidence of a fill.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="filledSize"/> is not greater than zero.</exception>
    public static TaggedFillEvidence Filled(
        string customTag, decimal filledSize, decimal? filledPrice = null, string? venueOrderKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customTag);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(filledSize, 0m);
        return new TaggedFillEvidence(TaggedFillStatus.Filled, customTag, filledSize, filledPrice, venueOrderKey);
    }

    /// <summary>The venue was reachable, carries fill history, and reports no fill under this tag.</summary>
    /// <param name="customTag">The tag that was asked about.</param>
    /// <returns>A positive no-fill report.</returns>
    public static TaggedFillEvidence NoFillFound(string customTag) =>
        new(TaggedFillStatus.NoFillFound, customTag);

    /// <summary>The venue can answer but this attempt failed. Never "did not fill".</summary>
    /// <param name="customTag">The tag that was asked about.</param>
    /// <returns>An unavailable answer.</returns>
    public static TaggedFillEvidence Unavailable(string customTag) =>
        new(TaggedFillStatus.Unavailable, customTag);

    /// <summary>This venue cannot answer fill-history questions at all.</summary>
    /// <param name="customTag">The tag that was asked about.</param>
    /// <returns>An unsupported answer.</returns>
    public static TaggedFillEvidence Unsupported(string customTag) =>
        new(TaggedFillStatus.Unsupported, customTag);
}
