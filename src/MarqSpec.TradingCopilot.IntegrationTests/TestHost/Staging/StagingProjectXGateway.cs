using System.Globalization;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Client.ProjectX.Configuration;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Aliased rather than a blanket `using MarqSpec.TradingCopilot.Domain.Venue;` — that namespace's own `OrderType`
// collides with the ProjectX client's `OrderType` (Api.Models), which every venue-truth read in this file already
// uses unqualified.
using FirmConventions = MarqSpec.TradingCopilot.Domain.Venue.FirmConventions;
using IOrderExecutor = MarqSpec.TradingCopilot.Domain.Venue.IOrderExecutor;
using OrderRequest = MarqSpec.TradingCopilot.Domain.Venue.OrderRequest;
using PlacedOrder = MarqSpec.TradingCopilot.Domain.Venue.PlacedOrder;
using TaggedFillEvidence = MarqSpec.TradingCopilot.Domain.Venue.TaggedFillEvidence;
using VenueAccountId = MarqSpec.TradingCopilot.Domain.Venue.VenueAccountId;
using VenueContractId = MarqSpec.TradingCopilot.Domain.Venue.VenueContractId;
using VenueId = MarqSpec.TradingCopilot.Domain.Venue.VenueId;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// A <b>direct read of ProjectX venue truth</b> for the bracket pre-live gates (gh#269, gh#293). The deployed app
/// exposes <b>no</b> resting-orders / bracket read — its only venue-truth <c>GET</c> reports positions, and the
/// venue-neutral <see cref="MarqSpec.TradingCopilot.Domain.Venue.WorkingOrder"/> view even <b>drops the size</b> — so
/// a gate that must witness the attached protective-stop leg (its trigger for gh#269, its <b>size</b> for gh#293)
/// reads the gateway's own open-orders endpoint. Built from the reserved <b>PRACTICE</b> credentials (CI secrets,
/// never source; R-14), it authenticates as the operator and reads the <b>same account the app placed onto</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the QA-side venue-truth reader the staging harness always anticipated: the app remains the only thing
/// that <i>places</i> or <i>modifies</i> a <b>valid</b> order (through its real gate — the whole point of the gate is
/// that nothing bypasses it), while the test observes the gateway's authoritative resting state. Its writes are
/// <see cref="FlattenAsync"/> — the caller's own cleanup, so a serialized rerun starts flat — and the one deliberate
/// exception below.
/// </para>
/// <para>
/// <b>The exception: <see cref="PlaceOrderAsync"/> (gh#668).</b> One question cannot be asked through the app at
/// all — <i>what does the venue do with an order it must reject?</i> The app's risk / execution gate refuses a
/// malformed ticket <b>before</b> it is transmitted, which is correct and is exactly why the venue would never see
/// it. Proving that a genuine venue rejection surfaces as <c>VenueRefusalKind.Definitive</c> therefore requires
/// transmitting through the production <see cref="ProjectXVenue"/> seam directly. This is <b>not</b> a licence to
/// bypass the gate for valid orders — see that method's own remarks.
/// </para>
/// </remarks>
internal sealed class StagingProjectXGateway : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IProjectXApiClient _api;
    private readonly IOrderExecutor _orderExecutor;

    private StagingProjectXGateway(ServiceProvider provider, IProjectXApiClient api, IOrderExecutor orderExecutor)
    {
        _provider = provider;
        _api = api;
        _orderExecutor = orderExecutor;
    }

    /// <summary>
    /// Builds a client over the reserved practice credentials (<c>STAGING_PROJECTX_API_KEY/SECRET</c>, optional
    /// <c>STAGING_PROJECTX_API_BASE_URL</c>). Gated behind <see cref="StagingGatewayFactAttribute"/>, so the config
    /// is guaranteed present when this runs. Also builds the production <see cref="ProjectXVenue"/> over the same
    /// client — <c>AddProjectXApiClient</c> registers <see cref="IProjectXWebSocketClient"/> too, and building it
    /// here does not open a connection (that only happens on an explicit <c>Connect*Async</c> call, which nothing
    /// here makes) — so <see cref="FindFilledOrderByTagAsync"/> below has a real production seam to call without
    /// paying for realtime plumbing this gate never uses.
    /// </summary>
    public static StagingProjectXGateway Create()
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            [$"{ProjectXOptions.SectionName}:ApiKey"] = StagingConfig.GatewayApiKey,
            [$"{ProjectXOptions.SectionName}:ApiSecret"] = StagingConfig.GatewayApiSecret,
        };
        if (!string.IsNullOrWhiteSpace(StagingConfig.GatewayBaseUrl))
        {
            settings[$"{ProjectXOptions.SectionName}:BaseUrl"] = StagingConfig.GatewayBaseUrl;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddProjectXApiClient(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        IProjectXApiClient api = provider.GetRequiredService<IProjectXApiClient>();

        // Simulated matches the practice-account context every staging gate operates in (R-14 — practice and
        // evaluation credentials are entitled to the simulated data universe); FirmConventions.None mirrors the
        // "nothing declared" default ProjectXVenueFactory itself falls back to. Neither is expected to matter for
        // an order-history read, but both are required constructor parameters regardless.
        IOrderExecutor orderExecutor = new ProjectXVenue(
            api,
            provider.GetRequiredService<IProjectXWebSocketClient>(),
            ProjectXDataTier.Simulated,
            FirmConventions.None);
        return new StagingProjectXGateway(provider, api, orderExecutor);
    }

    /// <summary>The gateway's own account id for the reserved practice account, matched by its venue name (R-14).</summary>
    public async Task<int> ResolveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        IEnumerable<TradingAccount> accounts = await _api.GetAccountsAsync(onlyActiveAccounts: true, cancellationToken);
        TradingAccount account = accounts.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, StagingConfig.PracticeAccountKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The reserved practice account '{StagingConfig.PracticeAccountKey}' is not visible to the gateway credentials.");
        return account.Id;
    }

    /// <summary>
    /// Resolves the active contract for a venue-neutral symbol (e.g. <c>MES</c>) — the id, tick size, and point
    /// value the app's order request needs, straight from the gateway so the test never hard-codes an instrument spec.
    /// </summary>
    public async Task<Contract> ResolveContractAsync(string symbol, CancellationToken cancellationToken = default)
    {
        IEnumerable<Contract> matches = await _api.SearchContractsAsync(symbol, live: true, cancellationToken);
        return matches.FirstOrDefault(contract => contract.ActiveContract)
            ?? matches.FirstOrDefault()
            ?? throw new InvalidOperationException($"No contract resolved for the execution instrument '{symbol}'.");
    }

    /// <summary>The most recent traded price for the contract — a live anchor for placing a resting limit off the market.</summary>
    public async Task<decimal> ReferencePriceAsync(string contractId, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        IEnumerable<AggregateBar> bars = await _api.GetHistoricalBarsAsync(
            contractId, now.AddHours(-8), now, AggregateBarUnit.Minute,
            unitNumber: 1, limit: 1, live: false, includePartialBar: true, cancellationToken: cancellationToken);
        AggregateBar? last = bars.OrderByDescending(bar => bar.Timestamp).FirstOrDefault();
        return last?.Close
            ?? throw new InvalidOperationException($"No recent bar for '{contractId}' to anchor an off-market limit.");
    }

    /// <summary>The orders resting live at the gateway for the account — the authoritative venue truth.</summary>
    public async Task<IReadOnlyList<Order>> OpenOrdersAsync(int accountId, CancellationToken cancellationToken = default) =>
        [.. await _api.GetOpenOrdersAsync(accountId, cancellationToken)];

    /// <summary>The open positions at the gateway for the account — signed by <see cref="Position.Type"/>, sized by <see cref="Position.Size"/>.</summary>
    public async Task<IReadOnlyList<Position>> OpenPositionsAsync(int accountId, CancellationToken cancellationToken = default) =>
        [.. await _api.GetOpenPositionsAsync(accountId, cancellationToken)];

    /// <summary>
    /// The single resting protective <b>stop</b> leg on the contract, if one stands — the attached-on-fill safety
    /// bracket materialised as a native stop order (a sell-stop protects a long, a buy-stop a short). Filters by
    /// <see cref="OrderType.Stop"/>; the serialized, flat-at-start account means at most one such leg is ours.
    /// </summary>
    public static Order? ProtectiveStop(IReadOnlyList<Order> openOrders, string contractIdHint)
    {
        ArgumentNullException.ThrowIfNull(openOrders);
        return openOrders.SingleOrDefault(order => order.Type == OrderType.Stop
            && order.ContractId.Contains(contractIdHint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The venue's <b>order-history</b> read for the account since <paramref name="since"/> — the only venue-truth
    /// surface that still shows an order once it has gone <b>terminal</b> (filled, cancelled, expired, rejected)
    /// and left the resting book. Added for the gh#643 fill-echo gate: once a round-tripped fill closes out and the
    /// account goes flat again, neither <see cref="OpenOrdersAsync"/> nor <see cref="OpenPositionsAsync"/> retains
    /// any trace of it — this is the one read that can.
    /// </summary>
    public async Task<IReadOnlyList<Order>> GetOrdersAsync(
        int accountId, DateTime since, CancellationToken cancellationToken = default) =>
        [.. await _api.GetOrdersAsync(accountId, since, endTime: null, cancellationToken)];

    /// <summary>
    /// The <b>executed</b> order-history record stamped with <paramref name="customTag"/>, if the venue's order
    /// history echoes the tag on it — <c>null</c> when no such record exists. "Executed" means
    /// <see cref="Order.FillVolume"/> is greater than zero; a partial fill still counts, mirroring the veto
    /// semantics of <c>TaggedFillEvidence</c> ("a partial fill is a fill").
    /// </summary>
    /// <remarks>
    /// Deliberately independent of — and never delegates to — the production
    /// <c>IOrderExecutor.FindFilledOrderByTagAsync</c> matching: gh#643 exists to prove the <b>venue</b>
    /// characteristic that method assumes (that ProjectX echoes <c>customTag</c> on a terminal record), not to
    /// re-run the production method's own logic against itself and call that proof.
    /// </remarks>
    public static Order? ExecutedOrderWithTag(IReadOnlyList<Order> orders, string customTag)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentException.ThrowIfNullOrWhiteSpace(customTag);
        return orders.FirstOrDefault(order =>
            string.Equals(order.CustomTag, customTag, StringComparison.Ordinal) && order.FillVolume > 0);
    }

    /// <summary>
    /// Calls the production <c>IOrderExecutor.FindFilledOrderByTagAsync</c> directly, against a real
    /// <see cref="ProjectXVenue"/> built over this gateway's authenticated client (gh#643). Unlike
    /// <see cref="ExecutedOrderWithTag"/> — which independently proves the venue echoes the tag, without ever
    /// touching production code — this exercises the production seam itself: its window handling, its own
    /// comparison, its mapping onto <see cref="TaggedFillEvidence"/>. The two are complementary diagnostics: if
    /// this call comes back red while <see cref="ExecutedOrderWithTag"/> still finds the record, the venue is
    /// fine and the production matching is wrong; if both come back red together, the venue itself does not echo
    /// the tag — the gh#643 assumption is false.
    /// </summary>
    public Task<TaggedFillEvidence> FindFilledOrderByTagAsync(
        int accountId, string customTag, DateTimeOffset since, CancellationToken cancellationToken = default) =>
        _orderExecutor.FindFilledOrderByTagAsync(VenueAccount(accountId), customTag, since, cancellationToken);

    /// <summary>
    /// Transmits <paramref name="request"/> through the <b>production</b>
    /// <see cref="ProjectXVenue.PlaceOrderAsync"/> — the real adapter, over the reserved practice credentials, with
    /// nothing faked between the ticket and the gateway (gh#668).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this bypasses the app, and why that is not a loophole.</b> Every other placement in the staging tier
    /// goes through the deployed API so the risk / execution gate stays in the loop, and that must remain the rule
    /// for anything <b>valid</b>. gh#668 asks the one question that rule makes unanswerable: whether a
    /// <i>genuine</i> venue rejection arrives as <see cref="MarqSpec.TradingCopilot.Domain.Venue.VenueRefusalKind.Definitive"/>.
    /// A deliberately-invalid ticket is refused by the app's own pre-flight first, so the venue never sees it and
    /// the <c>!success</c> arm of <see cref="ProjectXVenue.PlaceOrderAsync"/> is never reached. Reaching it means
    /// transmitting here.
    /// </para>
    /// <para>
    /// PRACTICE ONLY (R-14) — the credentials this gateway is built from are the reserved practice ones, and a live
    /// account is never wired into staging. A caller is nonetheless responsible for its own cleanup
    /// (<see cref="FlattenAsync"/>): if the venue accepts what the caller expected it to reject, an order rests.
    /// </para>
    /// </remarks>
    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) =>
        _orderExecutor.PlaceOrderAsync(request, cancellationToken);

    /// <summary>
    /// The single resting protective <b>take-profit</b> leg on the contract, if one stands — the OCO sibling of
    /// <see cref="ProtectiveStop"/>, materialised as a native <see cref="OrderType.Limit"/> order when the entry
    /// carried a profit target. Safe to filter on <c>Limit</c> only <b>after the entry has filled</b>: the entry
    /// itself is a resting limit until then, and on a serialized, flat-at-start account it is the only other one.
    /// </summary>
    public static Order? ProtectiveTakeProfit(IReadOnlyList<Order> openOrders, string contractIdHint)
    {
        ArgumentNullException.ThrowIfNull(openOrders);
        return openOrders.SingleOrDefault(order => order.Type == OrderType.Limit
            && order.ContractId.Contains(contractIdHint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Partially closes an open position at the gateway — the raw <c>partialCloseContract</c> call
    /// (<c>PartialClosePositionAsync</c>), issued <b>directly</b> against the reserved practice account (gh#1012).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this goes direct, and why that is not the gh#668 loophole widening.</b> The copilot has <b>no reduce
    /// path at all</b>: gh#928 — capability flag, <c>ReducePositionAsync</c>, <c>PositionReduceService</c>,
    /// <c>POST /accounts/{id}/positions/{instrument}/reduce</c> — is unimplemented, and gh#928's own text blocks it
    /// on <i>this</i> verification landing first. There is therefore no app surface to ask the question through,
    /// and the question — <i>what does ProjectX do to an attached OCO bracket when the position under it is
    /// partially closed?</i> — is a property of the <b>gateway</b>, not of any copilot code. It would read the same
    /// whether the reduce shipped or not.
    /// </para>
    /// <para>
    /// The entry this reduces is still placed <b>through the deployed app and its real risk gate</b>; only the
    /// partial close itself is issued here. PRACTICE ONLY (R-14). A partial close only ever <b>reduces</b>
    /// exposure, so a direct call cannot open a position the gate never saw — the property that makes the full
    /// exit gate-exempt in the first place (ADR-0007).
    /// </para>
    /// </remarks>
    public async Task<PartialClosePositionResponse> PartialCloseAsync(
        int accountId, string contractId, int size, CancellationToken cancellationToken = default) =>
        await _api.PartialClosePositionAsync(accountId, contractId, size, cancellationToken);

    /// <summary>The account's net signed quantity on the contract right now — positive long, negative short, 0 flat.</summary>
    public async Task<int> NetQuantityAsync(int accountId, string contractIdHint, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Position> positions = await OpenPositionsAsync(accountId, cancellationToken);
        Position? position = positions.FirstOrDefault(candidate =>
            candidate.ContractId.Contains(contractIdHint, StringComparison.OrdinalIgnoreCase));
        return position is null
            ? 0
            : position.Type == PositionType.Short ? -position.Size : position.Size;
    }

    /// <summary>
    /// The ProjectX-qualified account handle the venue-neutral seam takes, from the gateway's own integer account id
    /// (<see cref="ResolveAccountIdAsync"/>). Qualified rather than bare so a foreign handle cannot reach ProjectX on
    /// a colliding key (R-17).
    /// </summary>
    public static VenueAccountId VenueAccount(int accountId) =>
        VenueAccountId.Create(ProjectXVenueId, accountId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The ProjectX-qualified contract handle the venue-neutral seam takes, from a gateway contract id such as
    /// <c>CON.F.US.EP.M25</c> (<see cref="ResolveContractAsync"/>). The key is passed through opaquely — including a
    /// deliberately unknown one, which is the whole point of the gh#668 gate.
    /// </summary>
    public static VenueContractId VenueContract(string contractKey) =>
        VenueContractId.Create(ProjectXVenueId, contractKey);

    /// <summary>The venue every handle this gateway mints is tagged with — one login is one firm (ADR-0016).</summary>
    private static VenueId ProjectXVenueId { get; } = VenueId.Parse("projectx");

    /// <summary>
    /// Cleanup for a serialized rerun: closes any open position on the contract and cancels every order still resting
    /// on it (the entry, its protective stop, any take-profit leg), so the reserved account returns flat. Swallows a
    /// "nothing to close" refusal — an already-flat account is the goal, not an error.
    /// </summary>
    public async Task FlattenAsync(int accountId, string contractId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.ClosePositionAsync(accountId, contractId, cancellationToken);
        }
        catch (ProjectXApiException)
        {
            // No open position on the contract — already flat; nothing to close.
        }

        IReadOnlyList<Order> resting = await OpenOrdersAsync(accountId, cancellationToken);
        foreach (Order order in resting.Where(order =>
            order.ContractId.Contains(contractId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await _api.CancelOrderAsync(accountId, order.Id, cancellationToken);
            }
            catch (ProjectXApiException)
            {
                // The order already left the book between the read and the cancel — the flat goal still holds.
            }
        }
    }

    /// <summary>Whether the account holds a non-flat position on the contract right now.</summary>
    public async Task<bool> HasOpenPositionAsync(int accountId, string contractIdHint, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Position> positions = await OpenPositionsAsync(accountId, cancellationToken);
        return positions.Any(position => position.Size != 0
            && position.ContractId.Contains(contractIdHint, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
