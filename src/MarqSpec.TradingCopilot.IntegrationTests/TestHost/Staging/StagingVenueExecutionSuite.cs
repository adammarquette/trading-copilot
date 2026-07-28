using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Shared setup for the staging <b>venue-execution</b> gates (gh#269, gh#293): authenticate against the deployed API
/// and resolve the <b>reserved ProjectX practice account</b> (<c>STAGING_PROJECTX_PRACTICE_ACCOUNT</c>). Concrete
/// suites join <see cref="StagingExecutionCollection"/> so their order-placing runs are serialized onto that one
/// account. PRACTICE ONLY (R-14) — a live account is never wired here.
/// </summary>
public abstract class StagingVenueExecutionSuite
{
    /// <summary>An authenticated client against the deployed staging API.</summary>
    protected static Task<HttpClient> AuthenticatedClientAsync(CancellationToken cancellationToken = default) =>
        StagingApiClient.AuthenticatedAsync(cancellationToken);

    /// <summary>
    /// Resolves the reserved practice account id, creating the firm/connection on first run and reusing them after
    /// (staging is persistent, so setup is idempotent). Returns the account whose venue key matches the reserved one.
    /// </summary>
    protected static async Task<Guid> ResolvePracticeAccountAsync(HttpClient client)
    {
        Guid connectionId = await EnsureConnectionAsync(client);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connectionId}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.SingleOrDefault(a => a.VenueAccountKey == StagingConfig.PracticeAccountKey)
            ?? throw new InvalidOperationException(
                $"The reserved practice account '{StagingConfig.PracticeAccountKey}' was not discovered on the staging connection.");
        return account.Id;
    }

    private static async Task<Guid> EnsureConnectionAsync(HttpClient client)
    {
        using HttpResponseMessage list = await client.GetAsync("/connections");
        list.EnsureSuccessStatusCode();
        List<ConnectionResponse>? connections = await list.Content.ReadFromJsonAsync<List<ConnectionResponse>>(JsonSerializerOptions.Web);
        ConnectionResponse? existing = connections?.FirstOrDefault(c =>
            c.Platform == "projectx" && c.CredentialKey == StagingConfig.VenueCredentialKey);
        if (existing is not null)
        {
            return existing.Id;
        }

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest("Staging-Execution-Gate", FirmType.PropFirm));
        createFirm.EnsureSuccessStatusCode();
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", StagingConfig.VenueCredentialKey!));
        createConn.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Id;
    }

    /// <summary>
    /// Declares a permissive practice-account risk profile so the send/modify gate <b>approves</b> the small test
    /// quantities (the gate stays enforcing — this only makes the test size legal, never bypasses it). Sized-off the
    /// safety stop, generous per-trade caps, <paramref name="maxContractsPerOrder"/> as the only binding ceiling.
    /// </summary>
    protected static async Task DeclareRiskAsync(HttpClient client, Guid accountId, int maxContractsPerOrder)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 5_000m,
                AccountProfitTarget: 3_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: null,
                PerTradeRiskFraction: 0.5m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 5_000m,
                DailyDrawdownGovernor: 5_000m,
                DailyProfitTarget: null,
                StopForDayAtProfitTarget: false,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: maxContractsPerOrder,
                MaxBestDayFraction: null,
                StartingBalance: 50_000m));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Places a <b>resting long limit</b> below the market through the deployed app, carrying a safety stop so the app
    /// attaches the always-native protective bracket (R-5, gh#183). Returns the app's send response (the journaled
    /// order id). The entry sits <paramref name="ticksBelowMarket"/> below the reference so it rests <i>Working</i>,
    /// giving a window to modify before it fills.
    /// </summary>
    protected static async Task<SendOrderResponse> PlaceRestingLongAsync(
        HttpClient client,
        Guid accountId,
        string symbol,
        decimal tickSize,
        decimal pointValue,
        int quantity,
        decimal market,
        int ticksBelowMarket = 40)
    {
        decimal entry = market - (ticksBelowMarket * tickSize);
        decimal workingStop = entry - (10 * tickSize);
        decimal safetyStop = entry - (20 * tickSize);

        // ReferencePrice == Entry (zero fat-finger deviation): these gates test bracket behaviour, not the R-16 band,
        // so the deliberately-off-market resting entry must not be refused by it. The band is exercised by its own
        // suites (gh#283 etc.); here it must simply pass.
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders",
            new SendOrderRequest(
                Symbol: symbol,
                TickSize: tickSize,
                PointValue: pointValue,
                Side: OrderSide.Buy,
                Quantity: quantity,
                Entry: entry,
                Stop: workingStop,
                SafetyStop: safetyStop,
                ReferencePrice: entry,
                Type: OrderType.Limit));
        response.EnsureSuccessStatusCode();
        SendOrderResponse? sent = await response.Content.ReadFromJsonAsync<SendOrderResponse>(JsonSerializerOptions.Web);
        ArgumentNullException.ThrowIfNull(sent);
        return sent;
    }

    /// <summary>PATCHes a working order in place through the app (reprice, resize, or both).</summary>
    protected static async Task<HttpResponseMessage> ModifyAsync(
        HttpClient client, Guid orderId, ModifyWorkingOrderRequest request) =>
        await client.PatchAsJsonAsync($"/orders/{orderId}/price", request);

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout elapses — venue fills settle asynchronously,
    /// so venue-truth assertions wait for the gateway to reflect the fill rather than racing it.
    /// </summary>
    protected static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? pollEvery = null)
    {
        TimeSpan interval = pollEvery ?? TimeSpan.FromSeconds(1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(interval);
        }

        return await condition();
    }
}
