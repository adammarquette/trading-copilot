namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The facts committed <b>before</b> a position close is transmitted (gh#1161). The requested quantity lives
/// here so a crash or a caller abort after the venue executes cannot lose it.
/// </summary>
public sealed record PositionActionIntentDraft
{
    /// <summary>Which close was asked — the full exit or the sized reduce.</summary>
    public required PositionActionKind Action { get; init; }

    /// <summary>The affected account's owning operator (R-20).</summary>
    public required Guid OwnerUserId { get; init; }

    /// <summary>The platform account the close was requested against.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>The venue's own key for that account.</summary>
    public required string VenueAccountKey { get; init; }

    /// <summary>The instrument the operator named.</summary>
    public required string Instrument { get; init; }

    /// <summary>The venue contract, when the attempt had resolved one.</summary>
    public string? Contract { get; init; }

    /// <summary>How many contracts to take off, for a reduce. Null for an exit.</summary>
    public int? RequestedQuantity { get; init; }
}

/// <summary>
/// Commits and resolves the durable pre-transmit intent for the operator's per-position exit and reduce
/// (gh#1161). The send path's analog of <c>ConditionalStatus.Firing</c> / <c>OrderStatus.Taking</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CommitAsync"/> is on the critical path.</b> It must succeed before the venue is touched — a
/// failed commit refuses the send rather than transmitting without a row. It takes the caller's cancellation
/// token: cancelling before the venue is asked is a clean abort.
/// </para>
/// <para>
/// <b><see cref="ResolveSafelyAsync"/> is a secondary write.</b> The venue action has already happened, so a
/// resolve fault must never change the outcome the operator is told. It takes no cancellation token for the
/// same reason the #1160 journal does not.
/// </para>
/// </remarks>
public interface IPositionActionIntentStore
{
    /// <summary>Writes one Open intent in its own committed unit and returns its id.</summary>
    /// <param name="draft">What was asked, before the venue sees it.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task<Guid> CommitAsync(PositionActionIntentDraft draft, CancellationToken cancellationToken);

    /// <summary>Stamps the verified outcome on an Open intent. Never throws.</summary>
    /// <param name="intentId">The id <see cref="CommitAsync"/> returned.</param>
    /// <param name="outcome">The outcome name the operator was told.</param>
    /// <param name="resolvedAt">When the attempt resolved.</param>
    Task ResolveSafelyAsync(Guid intentId, string outcome, DateTimeOffset resolvedAt);
}
