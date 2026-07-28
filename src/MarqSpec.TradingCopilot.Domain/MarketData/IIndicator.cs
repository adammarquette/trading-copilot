using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// A bar-derived indicator (R-22, gh#310) — a named, period-parameterised projection over an OHLCV series.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately the shape <see cref="AverageTrueRange"/> already proved: one nullable value per
/// bar, aligned one-to-one, <see langword="null"/> until the period is satisfied. A <see cref="Compute"/> is a
/// <b>pure function of the bars handed in</b> — no clock, no storage, no state — which is what keeps ADR-0001's
/// "rebuild = replay" true: recomputing over the same clean-historical series reproduces the same values exactly.
/// </para>
/// <para>
/// <b>Period is part of the identity, so it lives on the instance</b>: an ATR(14) and an ATR(3) are different
/// numbers stored under different keys, and one configured instance maps to exactly one
/// <c>(Indicator, Period)</c> family of rows in the projection store. A multi-output indicator (MACD's line /
/// signal / histogram) is expressed later as several single-value indicators with distinct <see cref="Name"/>s
/// — the storage keys on the name — so this single-value contract needs no change to admit them.
/// </para>
/// </remarks>
public interface IIndicator
{
    /// <summary>The stored indicator name (row identity), lowercase and stable — e.g. <c>"atr"</c>, <c>"rsi"</c>.</summary>
    string Name { get; }

    /// <summary>The period parameter that is part of a value's identity (Wilder's default is 14).</summary>
    int Period { get; }

    /// <summary>
    /// Computes the indicator for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in <b>ascending</b> time order.</param>
    /// <returns>
    /// One entry per bar; <see langword="null"/> until the period is satisfied. No value is deliberate and better
    /// than a partial one — a half-warmed indicator looks ordinary and would mislead whatever reads it.
    /// </returns>
    IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars);
}
