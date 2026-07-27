using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Pre-merge telemetry <b>safety</b> coverage (gh#331 ⇒ gh#234, engineering §9): <b>observability must never be able
/// to break trading</b>. Two blunt guards — an <b>unreachable exporter</b> must not block a send / flatten / consume,
/// and the metric sink must be <b>total</b> (no recording path can throw), which is the contract the enforcing paths
/// rely on when they record without a try/catch. Container-backed Postgres + the adversarial venue via
/// <see cref="TelemetryDeadExporterPostgresFactory"/> (OTLP export enabled, pointed at a dead endpoint).
/// </summary>
public sealed class TelemetryIntegrationTests : IClassFixture<TelemetryDeadExporterPostgresFactory>
{
    private const string VenueKey = "PRAC-50K-101";

    private readonly TelemetryDeadExporterPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public TelemetryIntegrationTests(TelemetryDeadExporterPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Telemetry_ShouldNotBlockTrading_WhenExporterUnavailable()
    {
        // Arm the guard: export IS enabled and pointed at an unreachable collector. Without this the test proves
        // nothing (a disabled exporter can't block anything).
        TelemetryOptions telemetry = _factory.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        telemetry.ExportEnabled.Should().BeTrue("the OTLP exporter must be enabled for this to test an UNAVAILABLE one");

        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);

        // A send exercises the hot-path SLIs (gate decision + order-ack) with the collector dead — it must complete.
        using HttpResponseMessage send = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());
        send.StatusCode.Should().Be(HttpStatusCode.OK, "a send completes even though the telemetry collector is unreachable");

        // A flatten pass records its deadline SLI with the collector dead — it must complete, not add an export
        // timeout to a pass racing the CME close.
        Func<Task> flatten = async () =>
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
            await service.RunPassAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        };
        await flatten.Should().NotThrowAsync("a flatten pass completes even though the telemetry collector is unreachable");
    }

    [Fact]
    public void Metrics_ShouldBeTotal_OnEveryRecordingPath()
    {
        // The contract the enforcing paths depend on: nothing on IExecutionMetrics may throw (engineering §9), so a
        // metrics fault can never fail the trading action it was measuring. Exercise every recording path — across
        // ALL enum values and with adversarial magnitudes — against the real sink and assert none throws.
        IExecutionMetrics metrics = _factory.Services.GetRequiredService<IExecutionMetrics>();

        Action recordAll = () =>
        {
            foreach (GateOutcome outcome in Enum.GetValues<GateOutcome>())
            {
                metrics.RecordGateDecision(outcome, bindingLayer: null);
                foreach (RiskLayer layer in Enum.GetValues<RiskLayer>())
                {
                    metrics.RecordGateDecision(outcome, layer);
                }
            }

            foreach (FlattenTier tier in Enum.GetValues<FlattenTier>())
            {
                foreach (string flattenOutcome in new[]
                {
                    ExecutionMetrics.FlattenExecuted, ExecutionMetrics.FlattenNothingToDo, ExecutionMetrics.FlattenEscalated,
                    ExecutionMetrics.FlattenMissed, ExecutionMetrics.FlattenDisabled, "an-unrecognised-outcome",
                })
                {
                    metrics.RecordFlattenDeadline(tier, flattenOutcome);
                }

                metrics.RecordTimeToFlat(tier, TimeSpan.Zero);
                metrics.RecordTimeToFlat(tier, TimeSpan.FromHours(1));
            }

            metrics.RecordOrderAck(TimeSpan.Zero);
            metrics.RecordOrderAck(TimeSpan.FromSeconds(30));
            metrics.SetKillSwitchEngaged(engaged: true);
            metrics.SetKillSwitchEngaged(engaged: false);
            metrics.SetOrphanedStops(0);
            metrics.SetOrphanedStops(int.MaxValue);
            metrics.RecordRetentionGap(string.Empty); // edge: an unnamed consumer group must not throw
            metrics.RecordPipelineLag("consumer", TimeSpan.FromMilliseconds(250));
        };

        recordAll.Should().NotThrow("every IExecutionMetrics recording path is total — a metrics fault must never fail a trading action");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private async Task<HttpClient> FreshOperatorAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Telemetry-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.VenueAccountKey == VenueKey).Id;
    }

    private Task DeclareRiskProfileAsync(HttpClient client, Guid accountId) =>
        client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 1_000m,
                AccountProfitTarget: 3_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: 50_100m,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 300m,
                DailyDrawdownGovernor: 600m,
                DailyProfitTarget: 1_500m,
                StopForDayAtProfitTarget: true,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: 0.4m,
                StartingBalance: 50_000m));

    private static SendOrderRequest ValidProposal() => new(
        Symbol: "MES",
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: 1,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);
}
