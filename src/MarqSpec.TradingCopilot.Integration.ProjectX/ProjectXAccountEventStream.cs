using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;
using ProjectXWebSocket = MarqSpec.Client.ProjectX.WebSocket;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// The ProjectX account-event seam (R-17, gh#219): the gateway's <b>user hub</b> behind
/// <see cref="IAccountEventStream"/>. It connects the user hub, subscribes each account's order / position / trade
/// streams, and translates the gateway payloads onto the neutral <see cref="AccountEvent"/>s at this boundary — so
/// no vendor type crosses into the core. A <b>singleton</b> over the process's one websocket client, exactly as
/// <see cref="ProjectXConnection"/> is (one credential set per process, ADR-0015).
/// </summary>
/// <remarks>
/// The event → <see cref="IAsyncEnumerable{T}"/> bridge mirrors the quote stream's, with one deliberate difference:
/// the channel is <b>unbounded and never drops</b>. A quote is a replaceable snapshot, so the quote stream sheds
/// the oldest under back-pressure; an account event is <b>truth we cannot lose</b> — a dropped fill or rejection
/// would strand an order's status — so every event is buffered. Account-event volume is low, so this is safe.
/// Registration and teardown share the iterator's lifecycle: a sequence never enumerated cannot leave handlers and
/// subscriptions behind.
/// </remarks>
public sealed class ProjectXAccountEventStream : IAccountEventStream
{
    private readonly ProjectXWebSocket.IProjectXWebSocketClient _webSocket;

    /// <summary>Creates the seam over the process's realtime client.</summary>
    /// <param name="webSocket">The gateway's realtime client (a singleton — the user hub is process-wide).</param>
    public ProjectXAccountEventStream(ProjectXWebSocket.IProjectXWebSocketClient webSocket) => _webSocket = webSocket;

    /// <inheritdoc />
    public VenueId Venue { get; } = VenueId.Parse("projectx");

    /// <inheritdoc />
    public async IAsyncEnumerable<AccountEvent> StreamAsync(
        IReadOnlyCollection<VenueAccountId> accounts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        Channel<AccountEvent> events = Channel.CreateUnbounded<AccountEvent>(new UnboundedChannelOptions
        {
            // Never drops: unlike a quote, every account event is truth (a fill, a rejection) that losing would
            // strand an order. Volume is low, so an unbounded buffer is safe.
            SingleReader = true,
        });

        void OnOrder(object? sender, ClientModels.OrderUpdate update) =>
            events.Writer.TryWrite(ProjectXMapping.ToOrderStateEvent(update, Venue));
        void OnTrade(object? sender, ClientModels.TradeNotification trade) =>
            events.Writer.TryWrite(ProjectXMapping.ToFillEvent(trade, Venue));
        void OnPosition(object? sender, ClientModels.PositionUpdate position) =>
            events.Writer.TryWrite(ProjectXMapping.ToPositionEvent(position, Venue));

        // Attach before connecting, so no event fired during subscription setup is missed.
        _webSocket.OrderUpdateReceived += OnOrder;
        _webSocket.TradeNotificationReceived += OnTrade;
        _webSocket.PositionUpdateReceived += OnPosition;

        try
        {
            await _webSocket.ConnectUserHubAsync(cancellationToken);
            foreach (VenueAccountId account in accounts)
            {
                int accountId = ProjectXMapping.ToAccountId(account, Venue);
                await _webSocket.SubscribeToOrderUpdatesAsync(accountId, cancellationToken);
                await _webSocket.SubscribeToTradeNotificationsAsync(accountId, cancellationToken);
                await _webSocket.SubscribeToPositionUpdatesAsync(accountId, cancellationToken);
            }

            await foreach (AccountEvent accountEvent in events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return accountEvent;
            }
        }
        finally
        {
            // Runs on every exit, cancellation included, so a dropped subscription cannot leak a handler.
            _webSocket.OrderUpdateReceived -= OnOrder;
            _webSocket.TradeNotificationReceived -= OnTrade;
            _webSocket.PositionUpdateReceived -= OnPosition;

            foreach (VenueAccountId account in accounts)
            {
                int accountId = ProjectXMapping.ToAccountId(account, Venue);
                await _webSocket.UnsubscribeFromOrderUpdatesAsync(accountId, CancellationToken.None);
                await _webSocket.UnsubscribeFromTradeNotificationsAsync(accountId, CancellationToken.None);
                await _webSocket.UnsubscribeFromPositionUpdatesAsync(accountId, CancellationToken.None);
            }
        }
    }
}
