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
using TaggedFillEvidence = MarqSpec.TradingCopilot.Domain.Venue.TaggedFillEvidence;
using VenueAccountId = MarqSpec.TradingCopilot.Domain.Venue.VenueAccountId;
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
/// This is the QA-side venue-truth reader the staging harness always anticipated: the app remains the only thing
/// that <i>places</i> or <i>modifies</i> (through its real gate — the whole point of the gate is that nothing
/// bypasses it), while the test observes the gateway's authoritative resting state. The only writes this reader
/// performs are <see cref="FlattenAsync"/> — the caller's own cleanup, so a serialized rerun starts flat.
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
        int accountId, string customTag, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        VenueAccountId account = VenueAccountId.Create(
            VenueId.Parse("projectx"), accountId.ToString(CultureInfo.InvariantCulture));
        return _orderExecutor.FindFilledOrderByTagAsync(account, customTag, since, cancellationToken);
    }

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
