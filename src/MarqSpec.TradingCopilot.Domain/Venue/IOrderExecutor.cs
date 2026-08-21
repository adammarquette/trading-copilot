namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The <b>execution</b> slice of the venue abstraction (R-17) — the only place an order reaches a broker. A
/// data-only provider does not implement it.
/// </summary>
/// <remarks>
/// This interface transmits; it does not decide. The risk model, execution-time re-validation, and sanity caps
/// (R-5 / R-12 / R-16) run before anything here is called, and enforcement never lives below this line.
/// </remarks>
public interface IOrderExecutor : IVenue
{
    /// <summary>Transmits an order to the venue.</summary>
    /// <param name="request">The ticket to send — already gated by the risk and sanity layers.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's acknowledgement.</returns>
    Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a working order.</summary>
    /// <param name="account">The account the order sits on.</param>
    /// <param name="venueOrderId">The venue's order handle from <see cref="PlacedOrder.VenueOrderId"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the venue has accepted the cancellation.</returns>
    Task CancelOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies a <b>working</b> order in place (gh#259) — reprices and/or resizes it at the venue <b>without</b> a
    /// cancel/replace, so the order keeps its queue position and its attached protective bracket. A cancel/replace
    /// would surrender both and open a naked window between the pull and the re-send.
    /// </summary>
    /// <remarks>
    /// A <b>default-throwing capability</b>, like <see cref="GetWorkingOrdersAsync"/>: a venue that cannot modify
    /// in place refuses <b>loudly</b> (R-17) rather than silently doing nothing. An adapter that can — and that
    /// advertises <see cref="VenueCapability.ModifyOrder"/> — overrides this. Completes on the venue's acceptance;
    /// throws on refusal (the order may have filled or left the book, which the caller must not mislabel).
    /// </remarks>
    /// <param name="account">The account the order sits on.</param>
    /// <param name="venueOrderId">The venue's order handle from <see cref="PlacedOrder.VenueOrderId"/>.</param>
    /// <param name="limitPrice">The new limit price, or <see langword="null"/> to leave it unchanged.</param>
    /// <param name="stopPrice">The new stop / trigger price, or <see langword="null"/> to leave it unchanged.</param>
    /// <param name="size">The new size, or <see langword="null"/> to leave it unchanged.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">This venue cannot modify a working order in place.</exception>
    Task ModifyOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        Price? limitPrice,
        Price? stopPrice,
        int? size,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This venue does not support modifying a working order in place.");

    /// <summary>
    /// Closes a position outright — the primitive auto-flatten is built on (R-13). Returns the venue's own
    /// post-close view so the caller reconciles against the venue rather than trusting a local belief (ADR-0013).
    /// </summary>
    /// <param name="account">The account holding the position.</param>
    /// <param name="contract">The contract to flatten.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's position snapshot after the close.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The venue does not grant <see cref="VenueCapability.ClosePosition"/>.
    /// </exception>
    Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reduces a position by <paramref name="quantity"/> contracts — a sized partial close <b>toward flat</b>
    /// (gh#928, R-11), the primitive the operator's per-position reduce sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>reducing</b> action: it lowers exposure, so it rides the same native close seam as
    /// <see cref="ClosePositionAsync"/> and <b>not</b> the order-placement path. An opposing order would be a
    /// <i>send</i>, gated by R-5 / R-16 / R-12 — and a gate refusing a risk-<i>lowering</i> action is the wrong
    /// answer, a different thing from refusing to <i>raise</i> risk (gh#928). This transmits a close; it does not
    /// decide.
    /// </para>
    /// <para>
    /// A <b>default-throwing capability</b>, like <see cref="ModifyOrderAsync"/>: a venue that cannot size a
    /// partial close refuses <b>loudly</b> (R-17) rather than silently closing the whole position or doing
    /// nothing. An adapter that can — and advertises <see cref="VenueCapability.ReducePosition"/> — overrides
    /// this. Returns the venue's own post-reduce snapshot so the caller <b>verifies</b> the reduction against
    /// venue truth (ADR-0013), never assumes it: a partial close the venue accepted while still reporting the
    /// original size is not a reduction, and must not read as done.
    /// </para>
    /// </remarks>
    /// <param name="account">The account holding the position.</param>
    /// <param name="contract">The contract to reduce.</param>
    /// <param name="quantity">How many contracts to take off — a positive count. Sizing is the caller's.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's position snapshot after the partial close.</returns>
    /// <exception cref="NotSupportedException">This venue cannot size a partial close.</exception>
    Task<PositionSnapshot> ReducePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        int quantity,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This venue does not support reducing a position by a sized partial close.");

    /// <summary>
    /// Lists the orders <b>resting live at the venue</b> for an account (gh#183) — the venue's own truth, used by
    /// OCO-cancel-on-exit to find the protective legs still standing on a contract that has gone flat.
    /// </summary>
    /// <remarks>
    /// A <b>fail-closed default</b>: a venue that cannot list its working orders throws rather than silently
    /// reporting none, so a caller degrades <b>loudly</b> (R-17) instead of leaving a dangling leg it never saw.
    /// An adapter that can list overrides this.
    /// </remarks>
    /// <param name="account">The account whose working orders to list.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The orders resting at the venue for the account.</returns>
    /// <exception cref="NotSupportedException">This venue cannot list working orders.</exception>
    Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This venue does not support listing working orders.");

    /// <summary>
    /// Asks whether an order stamped with <paramref name="customTag"/> ever <b>filled</b> (gh#631) — the fill-level
    /// counterpart to <see cref="GetWorkingOrdersAsync"/>, which by construction can only see orders still resting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because "nothing rests under this tag" and "this order never reached the market" are different
    /// statements. A filled entry is no longer a working order, and once its bracket closes the position the account
    /// reads flat again — so a fire that placed, filled and round-tripped is indistinguishable, through the resting
    /// and position reads alone, from one that never placed at all. Only fill history separates them.
    /// </para>
    /// <para>
    /// <b>Why this defaults to <see cref="TaggedFillStatus.Unsupported"/> rather than throwing, unlike its
    /// fail-closed sibling above.</b> <see cref="GetWorkingOrdersAsync"/> throws because its answer <i>authorises</i>
    /// something — a caller acts on the belief that no protective leg is standing, so silence must be loud. This
    /// answer authorises nothing: per <see cref="TaggedFillEvidence"/> it is a <b>veto only</b>, so the worst a
    /// missing capability can do is leave the caller with the behaviour it already had. Throwing would instead turn
    /// every venue without fill history into a permanently unreconcilable one, which is a strictly worse outcome
    /// than the status quo it is trying to protect.
    /// </para>
    /// <para>
    /// An implementation must return <see cref="TaggedFillStatus.Unavailable"/> — never
    /// <see cref="TaggedFillStatus.NoFillFound"/> — when it cannot complete the read.
    /// </para>
    /// </remarks>
    /// <param name="account">The account the order was placed on.</param>
    /// <param name="customTag">The tag stamped on the order at transmit time.</param>
    /// <param name="since">
    /// How far back to search. The caller supplies it from the row's own durable transmit instant, so the window
    /// always covers the attempt.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the venue could tell us — never <see langword="null"/>.</returns>
    Task<TaggedFillEvidence> FindFilledOrderByTagAsync(
        VenueAccountId account,
        string customTag,
        DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TaggedFillEvidence.Unsupported(customTag));
}
