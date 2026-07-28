using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// <b>Instrumentation must never be able to break trading</b> (gh#331 ⇒ gh#26, ADR-0002, engineering §9): the thing
/// meant to <i>report</i> trouble must never become the trouble. Two faults are injected at the seams the system
/// really depends on — an <b>unreachable OTLP exporter</b> and a <b>throwing metrics sink</b> — and the question in
/// each case is whether a trade still completes.
/// </summary>
/// <remarks>
/// <para>
/// The whole suite runs under a <b>configured but unreachable</b> OTLP endpoint (see
/// <see cref="InstrumentationSafetyPostgresFactory"/>) — deliberately harder than the "no exporter configured" case
/// ADR-0002 already records as verified, and the state any deployment is in whenever its collector is down.
/// </para>
/// <para>
/// The metrics sink is faulted at the <b>DI seam</b> (<c>IExecutionMetrics</c>), not simulated: the interface gh#316
/// introduced is exactly what a future exporter-backed or decorating sink would be registered as, so a throwing
/// implementation is a reachable state rather than a hypothetical one.
/// </para>
/// <para>
/// <b>Three of these guards began as pinned DEFECTS and are now gh#343's regression guards (flipped in gh#382).</b>
/// As first written they recorded the system failing a trade because a metric throw was unguarded; the fix
/// (<c>FailureTolerantExecutionMetrics</c>) absorbs the fault, so each now asserts that the trade <b>completes</b>.
/// </para>
/// <para>
/// <b>The fault is injected inside that decorator, not in place of it.</b> Between gh#343 landing and gh#382 these
/// pins passed <i>vacuously</i>: the harness re-registered <c>IExecutionMetrics</c> with the bare fault injector,
/// which removed the shipped guard from the host, so the suite kept observing — and blessing — the pre-fix
/// behaviour. <see cref="Harness_ShouldWrapTheFaultInjector_InTheSameDecoratorProductionRegisters"/> exists so that
/// cannot recur silently.
/// </para>
/// </remarks>
public class InstrumentationSafetyIntegrationTests : IClassFixture<InstrumentationSafetyPostgresFactory>
{
    private readonly InstrumentationSafetyPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public InstrumentationSafetyIntegrationTests(InstrumentationSafetyPostgresFactory factory)
    {
        _factory = factory;

        // Faults cleared FIRST, so the disengage below cannot itself trip an armed gauge.
        _factory.Metrics.Reset();

        // A prior case's halt must not leak into this one. Worth noting WHY it can: KillSwitch.Engage assigns its
        // state before writing the gauge, so the kill-switch case below leaves the switch genuinely ENGAGED even
        // though its endpoint reported a failure — the halt took effect and the operator was told it did not.
        _factory.Services.GetRequiredService<KillSwitch>().Disengage();
        VenueFactory.ResetPositions();
    }

    [Fact]
    public async Task Telemetry_ShouldNotBlockTrading_WhenTheExporterIsUnreachable()
    {
        // The load-bearing degradation property (ADR-0002): the SDK is wired and every export attempt fails against
        // a dead endpoint, but the trading path must be untouched. An exporter that blocked on export would add its
        // timeout to a send — and, on the flatten path, to a pass racing the CME close.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        int before = VenueFactory.TotalPlacedOrderCount;

        Stopwatch clock = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unreachable collector must not fail a trade");
        VenueFactory.TotalPlacedOrderCount.Should().Be(before + 1, "the order still reached the venue");
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(20), "a dead exporter must not add its retry/timeout budget to the send path");
        (await OrderCountAsync(accountId)).Should().Be(1, "and the send was journaled as usual");
    }

    [Fact]
    public async Task Telemetry_ShouldNotFailTheSend_WhenTheSinkThrowsBeforeTheVenueIsReached()
    {
        // A sink that throws while counting the GATE decision, i.e. before anything is transmitted. Engineering §9:
        // "a metrics failure is logged and the action completes". Regression guard for gh#343 (was a pinned defect).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        _factory.Metrics.ThrowOnGateDecision = true;
        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a throwing metrics sink at the gate must not fail the send (gh#343, engineering §9)");

        // Note the direction of travel: BEFORE the fix this assertion read `before` — the throw aborted the send, so
        // nothing was transmitted, and that was the one consoling thing about the defect. Absorbing the fault means
        // the send now runs to completion, so the order genuinely reaches the venue and is journaled. Asserting the
        // old value here would pass only while the bug was present.
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            before + 1, "the send completed, so the order reached the venue");
        (await OrderCountAsync(accountId)).Should().Be(1, "and it was journaled as usual — the measurement is what was lost");
    }

    [Fact]
    public async Task Telemetry_ShouldNotFailTheSend_WhenTheSinkThrowsAfterTheVenueAcknowledged()
    {
        // THE DANGEROUS ONE. The ack latency is measured AFTER the venue accepted the order and BEFORE the journal
        // is written, so a throwing sink there leaves a LIVE order at the broker with no Order row and no stop plan
        // — while the operator is told the send failed. That is the orphaned-exposure hazard the whole system is
        // built to prevent, caused by instrumentation.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        _factory.Metrics.ThrowOnOrderAck = true;
        int before = VenueFactory.TotalPlacedOrderCount;

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", ValidProposal());

        // The high-severity half of gh#343, now its regression guard: the live order must be journaled and the
        // operator told the truth. A regression here re-creates orphaned exposure caused by instrumentation.
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a throwing sink after the venue ack must not fail the send (gh#343)");
        VenueFactory.TotalPlacedOrderCount.Should().Be(
            before + 1, "the order IS live at the venue — the transmit already succeeded");
        (await OrderCountAsync(accountId)).Should().Be(
            1,
            "and it MUST be journaled: the whole hazard was a live order with no Order row and no stop plan, "
            + "reported to the operator as a failure");
    }

    [Fact]
    public async Task Telemetry_ShouldNotFailTheKillSwitch_WhenTheSinkThrows()
    {
        // The operator's emergency halt is the last control that must never be blocked by a reporting concern. The
        // gauge is set inside KillSwitch.Engage, unguarded.
        HttpClient client = await AuthenticatedClientAsync();
        _factory.Metrics.ThrowOnKillSwitch = true;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/kill-switch",
            new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: "instrumentation-safety probe"));

        // gh#343's regression guard: the halt engages even when its gauge cannot be written. The original defect was
        // doubly bad — the switch DID engage and the operator was told it had not, so the reported state and the
        // real state disagreed on the one control that must never be ambiguous. Both halves are asserted.
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a throwing gauge must not fail the operator's emergency halt — the one control that must always work (gh#343)");
        _factory.Services.GetRequiredService<KillSwitch>().IsEngaged.Should().BeTrue(
            "and the halt really took effect — the operator's view and the system's state must agree");
    }

    [Fact]
    public void Harness_ShouldWrapTheFaultInjector_InTheSameDecoratorProductionRegisters()
    {
        // The guard on the guards (gh#382). Every fault-injection case above is only meaningful if the fault is
        // raised INSIDE the shipped FailureTolerantExecutionMetrics; a harness that registers its double as
        // IExecutionMetrics outright deletes that decorator from the host, and the three cases then pass whatever
        // production does. That is exactly what happened between gh#343 landing and gh#382 — the pins sat green over
        // a system nobody was deploying. This asserts the registration the factory DISPLACED was itself the
        // decorator, so the chain being reproduced is production's and not an invention of this test project.
        _factory.DisplacedRegistration.Should().NotBeNull(
            "the composition root must register IExecutionMetrics — if it stopped, the harness is reproducing nothing");

        object? production = _factory.DisplacedRegistration!.ImplementationFactory?.Invoke(_factory.Services)
            ?? _factory.DisplacedRegistration.ImplementationInstance;

        production.Should().BeOfType<FailureTolerantExecutionMetrics>(
            "production binds IExecutionMetrics to the failure-tolerant decorator (gh#343), which is the shape this "
            + "harness reproduces with the fault injector as the inner sink; if production's chain changes, these "
            + "cases must be re-pointed at the new one rather than silently testing the old");

        _factory.Services.GetRequiredService<IExecutionMetrics>().Should().BeOfType<FailureTolerantExecutionMetrics>(
            "the host these cases actually run against must be wrapped the same way — this is the assertion that "
            + "catches a harness which swaps the decorator out instead of wrapping the fault injector in it");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers — the send fixture mirrors the gh#182 / gh#282 harness.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        Guid accountId = accounts.First(account => account.CanTrade).Id;

        using HttpResponseMessage risk = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 1_000m,
                AccountProfitTarget: 3_000m,
                StartingBalance: 50_000m,
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
                MaxBestDayFraction: 0.4m));
        risk.EnsureSuccessStatusCode();
        return accountId;
    }

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

    private async Task<int> OrderCountAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await db.Orders.IgnoreQueryFilters().CountAsync(order => order.AccountId == accountId);
    }
}
