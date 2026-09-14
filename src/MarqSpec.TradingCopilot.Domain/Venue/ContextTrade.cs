namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A <b>last-trade print</b> from a cross-asset context source (gh#496, R-1) — the price a trade actually
/// happened at, with no book behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a <see cref="Quote"/>.</b> A quote is top-of-book and its bid/ask are prices something
/// could transact at; this is a print of something that already did. Finnhub's free tier publishes trades and no
/// book at all, so representing one as a <see cref="Quote"/> would mean asserting <c>Bid == Ask</c> — a zero
/// spread nobody observed, in the one stream <c>StopPromotionService</c> and <c>ConditionalFiringService</c> act
/// on. The two are kept apart in the type system so that can never happen by accident (operator decision,
/// 2026-08-02; engineering §9 "never fabricate a number").
/// </para>
/// <para>
/// Context is for correlation — SPY against ES, QQQ against NQ — and is <b>never an executable price</b>. Nothing
/// on the execution path may size, gate, or trigger from one.
/// </para>
/// </remarks>
/// <param name="Timestamp">When the trade printed at the source.</param>
/// <param name="Price">The price it printed at.</param>
/// <param name="Size">
/// How much traded, where the source publishes it — <see langword="null"/> when it does not. Absent rather than
/// zero, the same distinction <see cref="Quote.BidSize"/> draws: a source that says nothing about size is not a
/// source reporting a size of none.
/// <para>
/// <see cref="decimal"/> rather than the <see cref="long"/> a quote's resting size uses, because a context feed
/// is not restricted to whole contracts — the same providers carry fractional-share and crypto venues, and
/// rounding a print's size to an integer would quietly report a trade that did not happen.
/// </para>
/// </param>
public sealed record ContextTrade(DateTimeOffset Timestamp, Price Price, decimal? Size);
