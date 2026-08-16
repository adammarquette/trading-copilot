using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Recovery;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>runtime reconcile-strand detection sweep</b> (gh#923, of gh#722; R-11, R-13,
/// R-20; ADR-0013's 2026-08-15 update, ADR-0007) against real PostgreSQL. The named failure mode: an order left
/// <c>Taking</c> or a conditional left <c>Firing</c> mid-send — <b>possibly live at the venue with no journal behind
/// it</b> — forms while the process is up and sits there unseen, because the only two detectors were the operator's
/// own eye and the restart rehydrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Propose-and-confirm.</b> The behaviour under test is detect-and-alert only: the sweep must transmit nothing,
/// adopt nothing, release nothing and change no order state — the operator resolves the strand through the existing
/// <c>POST /orders/{id}/reconcile</c> / <c>POST /conditionals/{id}/reconcile</c>. Every case therefore asserts the
/// seeded row's <c>Status</c> is <b>unmoved</b>, alongside what was detected.
/// </para>
/// <para>
/// <b>How a pass is driven.</b> The host is constructed from DI exactly as the composition root builds it (root
/// provider + the singleton <see cref="ReconcileStrandRegister"/> + <see cref="IExecutionMetrics"/> +
/// <see cref="IOptions{TOptions}"/>) and started, so the whole pass — the cross-owner EF discovery, the pure
/// decision, the alert and the meter — is the production path end to end. The clock is the sweep's own
/// <c>UtcNow</c>, so age is controlled the only other way the register allows: a <b>one-second</b> bound over a
/// one-second cadence, which makes the first pass the strand's first-seen and a later pass its alert. Waits poll for
/// the observable outcome rather than sleeping a fixed span, so a slow CI runner cannot flake them.
/// </para>
/// <para>
/// <b>The prove-red control is in the suite</b> (<see cref="Sweep_ShouldNeverDetectAStrand_WhenNoSweepIsRunning"/>):
/// the same strand, the same observation channels, the only difference being whether the gh#722 host runs. Passive,
/// it must stay undetected — the pre-#722 world, where a runtime strand is never auto-detected; then, with the sweep
/// started, the very same strand alerts. Neither half can pass if the observation channel is dead or spurious.
/// </para>
/// </remarks>
public class ReconcileStrandSweepIntegrationTests : IClassFixture<ReconcileSweepTestPostgresFactory>
{
    private const string Contract = "CON.F.US.MES.U26";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    /// <summary>The strand age bound the driven passes use — the smallest the production option validation allows.</summary>
    private const int AgedBoundSeconds = 1;

    /// <summary>An age bound no test can outlive, for the "discovered but too fresh to alert" case.</summary>
    private const int UnreachableBoundSeconds = 3_600;

    /// <summary>How long a poll waits for an outcome the sweep should produce — generous; the loop exits on success.</summary>
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(30);

    /// <summary>How long the suite watches for an outcome that must <b>never</b> arrive — several passes' worth.</summary>
    private static readonly TimeSpan _inertWindow = TimeSpan.FromSeconds(6);

    private readonly ReconcileSweepTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public ReconcileStrandSweepIntegrationTests(ReconcileSweepTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Sweep_ShouldAlertOnceAndMeter_WhenAnOrderIsStrandedTakingPastTheAgeBound()
    {
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid orderId = await SeedTakingOrderAsync(accountId, owner);

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(orderId).Count >= 1)).Should().BeTrue(
                "an order still Taking past the age bound is a maybe-live intent with no journal behind it — the sweep must detect and alert it (gh#923)");
        }

        CapturedLog alert = AlertsNaming(orderId).Should().ContainSingle(
            "a strand pages the operator once, not once per pass").Subject;
        alert.Level.Should().Be(LogLevel.Critical, "a maybe-live strand is an operator emergency, not an informational line");
        alert.Message.Should().Contain("synthetic_risk", "the alert rides the rehydrator's operator-alert channel");
        alert.Message.Should().Contain(owner.ToString(), "the alert names the owning operator (R-20)");
        alert.Message.Should().Contain(orderId.ToString(), "the alert names the entity the operator must reconcile");
        alert.Message.Should().Contain("POST /orders/{id}/reconcile", "the alert names the route that resolves it — propose-and-confirm");

        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().ContainSingle(
            "the strand-detected counter increments exactly once, tagged order-taking")
            .Which.Value.Should().Be(1);

        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Taking, "the sweep detects and alerts — it never moves the order");
        (await OrderVenueKeyAsync(orderId)).Should().BeNull("nothing was transmitted or adopted");
    }

    [Fact]
    public async Task Sweep_ShouldAlertOnceAndMeter_WhenAConditionalIsStrandedFiringPastTheAgeBound()
    {
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId, owner);

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(conditionalId).Count >= 1)).Should().BeTrue(
                "a conditional still Firing past the age bound is the same maybe-live strand on the conditional path (gh#923)");
        }

        CapturedLog alert = AlertsNaming(conditionalId).Should().ContainSingle().Subject;
        alert.Level.Should().Be(LogLevel.Critical);
        alert.Message.Should().Contain("synthetic_risk");
        alert.Message.Should().Contain(owner.ToString(), "the alert names the owning operator (R-20)");
        alert.Message.Should().Contain(conditionalId.ToString());
        alert.Message.Should().Contain(
            "POST /conditionals/{id}/reconcile", "a mid-fire conditional resolves through the conditional route, not the order one");

        StrandMeasurements(ExecutionMetrics.ReconcileStrandConditionalFiring).Should().ContainSingle(
            "the strand-detected counter is dimensioned by kind — a firing conditional meters as conditional-firing")
            .Which.Value.Should().Be(1);
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().BeEmpty(
            "no order was stranded in this case — the kind tag must not be mis-routed");

        (await ConditionalStatusAsync(conditionalId)).Should().Be(
            ConditionalStatus.Firing, "the sweep never releases or re-fires a stranded conditional — the operator does");
    }

    [Fact]
    public async Task Sweep_ShouldDiscoverAndNameTheOwner_WhenTheStrandBelongsToAnotherOperator()
    {
        // The discovery is background plumbing with no request user, so it reads with IgnoreQueryFilters — past the
        // R-20 default-deny filter. The failure mode this guards is a sweep that silently sees only the bootstrap
        // operator's rows: a second operator's maybe-live strand would then never be detected at all. Ownership must
        // survive discovery onto the key and into the alert (R-20).
        (Guid _, Guid bootstrapOwner) = await FreshSurfaceAsync();
        (HttpClient second, Guid secondOwner) = await CreateSecondOperatorAsync();
        Guid secondAccount = await SetupAccountAsync(second, $"Topstep-Strand-B-{Guid.NewGuid():N}");
        Guid strandedForSecond = await SeedTakingOrderAsync(secondAccount, secondOwner);

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(strandedForSecond).Count >= 1)).Should().BeTrue(
                "a strand owned by another operator must still be found — the background read bypasses the R-20 filter (gh#923)");
        }

        secondOwner.Should().NotBe(bootstrapOwner, "the fixture must genuinely exercise a second owner");
        AlertsNaming(strandedForSecond).Should().ContainSingle()
            .Which.Message.Should().Contain(secondOwner.ToString(), "the alert carries the strand's own owner, never the sweeping process's");

        ReconcileStrandKey key = new(ReconcileStrandKind.OrderTaking, secondOwner, strandedForSecond);
        Register.Snapshot().Should().ContainKey(key, "the register keys the strand by its owner (R-20), so two operators' strands can never collide");

        (await OrderStatusAsync(strandedForSecond)).Should().Be(OrderStatus.Taking, "another operator's strand is detected, never acted on");
    }

    [Fact]
    public async Task Sweep_ShouldAlertExactlyOnce_WhenTheStrandPersistsAcrossManyPasses()
    {
        // Idempotency is what makes a loud alert survivable: a strand the operator has not yet reconciled stays
        // stranded for as long as it takes, and a sweep that re-paged every pass would be muted within minutes.
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid persistent = await SeedTakingOrderAsync(accountId, owner);

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(persistent).Count >= 1)).Should().BeTrue("the persistent strand alerts once it ages");

            // A SECOND strand, seeded while the first is still stranded and still discovered every pass. Waiting for
            // ITS alert proves the sweep kept passing over the first — otherwise "alerted once" would be satisfied by
            // a sweep that simply stopped running, the false green this construction rules out.
            Guid later = await SeedTakingOrderAsync(accountId, owner);
            (await WaitUntilAsync(() => AlertsNaming(later).Count >= 1)).Should().BeTrue(
                "a strand that appears later ages and alerts on its own clock — proving passes continued over the first");

            AlertsNaming(persistent).Should().ContainSingle(
                "a continuously-stranded intent alerts EXACTLY once across every pass it survives (gh#923)");
        }

        AlertsNaming(persistent).Should().ContainSingle("and it is still exactly once after the sweep stops");
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().HaveCount(
            2, "the counter follows the alert: one measurement per strand detected, never one per pass");
        (await OrderStatusAsync(persistent)).Should().Be(OrderStatus.Taking, "repeated passes still never move the order");
    }

    [Fact]
    public async Task Sweep_ShouldDiscoverButNotAlert_WhenTheStrandIsYoungerThanTheAgeBound()
    {
        // A take in flight IS Taking for a moment. Alerting on it would page the operator for healthy traffic and
        // make the signal worthless, so the bound is what separates "stuck" from "in flight".
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid inFlight = await SeedTakingOrderAsync(accountId, owner);
        ReconcileStrandKey key = new(ReconcileStrandKind.OrderTaking, owner, inFlight);

        await using (SweepRun run = await StartSweepAsync(UnreachableBoundSeconds))
        {
            (await WaitUntilAsync(() => Register.Snapshot().ContainsKey(key))).Should().BeTrue(
                "the fresh strand is DISCOVERED and clocked — it is only the alert that waits for the bound");
            await Task.Delay(_inertWindow); // several further passes, all of them well inside the bound
        }

        Register.Snapshot()[key].Alerted.Should().BeFalse("a strand younger than the bound has not been alerted");
        AlertsNaming(inFlight).Should().BeEmpty("an in-flight take must never page the operator (gh#923)");
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().BeEmpty(
            "and nothing is metered either — the counter counts alerts, not discoveries");
        (await OrderStatusAsync(inFlight)).Should().Be(OrderStatus.Taking, "the in-flight order is untouched");
    }

    [Fact]
    public async Task Sweep_ShouldForgetAResolvedStrand_AndReAgeTheSameIdWhenItReappears()
    {
        // The operator reconciles the strand and the row leaves Taking. The register must forget it — otherwise a
        // later, genuinely NEW episode on the same id would arrive pre-aged and pre-alerted, and never page at all.
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid orderId = await SeedTakingOrderAsync(accountId, owner);
        ReconcileStrandKey key = new(ReconcileStrandKind.OrderTaking, owner, orderId);

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(orderId).Count >= 1)).Should().BeTrue("the first episode alerts");

            await SetOrderStatusAsync(orderId, OrderStatus.Working); // the operator reconciled it: adopted as working
            (await WaitUntilAsync(() => !Register.Snapshot().ContainsKey(key))).Should().BeTrue(
                "a strand that has left Taking is resolved — the next pass drops it from the register");

            await SetOrderStatusAsync(orderId, OrderStatus.Taking); // a NEW episode on the same id
            (await WaitUntilAsync(() => AlertsNaming(orderId).Count >= 2)).Should().BeTrue(
                "a reappearance is re-aged and alertable afresh — a second episode deserves its own page (gh#923)");
        }

        AlertsNaming(orderId).Should().HaveCount(2, "exactly two episodes, exactly two alerts");
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().HaveCount(
            2, "the counter tracks the alerts — one per episode");
    }

    [Fact]
    public async Task Sweep_ShouldChangeNoState_WhenItDetectsStrandsOfBothKinds()
    {
        // The invariant the ratified propose-and-confirm decision rests on (ADR-0013): the sweep is a detector. It
        // must not adopt, release, cancel, transmit, or touch anything it did not come to look at.
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid strandedOrder = await SeedTakingOrderAsync(accountId, owner);
        Guid strandedConditional = await SeedFiringConditionalAsync(accountId, owner);
        Guid bystander = await SeedOrderAsync(accountId, owner, OrderStatus.Staged);

        int placedBefore = VenueFactory.TotalPlacedOrderCount;
        int cancelledBefore = VenueFactory.CancelOrderCalls.Count;
        int closedBefore = VenueFactory.ClosePositionCalls.Count;
        long ordersBefore = await OrderCountAsync();

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(strandedOrder).Count >= 1 && AlertsNaming(strandedConditional).Count >= 1))
                .Should().BeTrue("both strands are detected");
            await Task.Delay(_inertWindow); // and several further passes get their chance to act
        }

        (await OrderStatusAsync(strandedOrder)).Should().Be(OrderStatus.Taking, "no adopt, no release — the Taking order is exactly as it was");
        (await OrderVenueKeyAsync(strandedOrder)).Should().BeNull("nothing was adopted onto the order");
        (await ConditionalStatusAsync(strandedConditional)).Should().Be(ConditionalStatus.Firing, "the Firing conditional is exactly as it was");
        (await OrderStatusAsync(bystander)).Should().Be(OrderStatus.Staged, "a row the sweep did not come for is untouched");
        (await OrderCountAsync()).Should().Be(ordersBefore, "the sweep creates no order and deletes none");

        VenueFactory.TotalPlacedOrderCount.Should().Be(placedBefore, "the sweep transmits nothing — propose-and-confirm (ADR-0013)");
        VenueFactory.CancelOrderCalls.Should().HaveCount(cancelledBefore, "and cancels nothing");
        VenueFactory.ClosePositionCalls.Should().HaveCount(closedBefore, "and closes nothing");
    }

    [Fact]
    public async Task Sweep_ShouldStayInert_WhenDisabledByConfiguration()
    {
        // Asserted against the PRODUCTION-registered host (config → options → hosted service), which this fixture
        // configures Enabled=false on a 1s/1s cadence: enabled, it would alert the seeded strand within a couple of
        // seconds, so "nothing happened" here is a fact about the flag and not about the wait being too short.
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid orderId = await SeedTakingOrderAsync(accountId, owner);
        ReconcileStrandKey key = new(ReconcileStrandKind.OrderTaking, owner, orderId);

        await Task.Delay(_inertWindow); // no sweep is started by the test: the only candidate is the disabled one

        Register.Snapshot().Should().NotContainKey(key, "a disabled sweep discovers nothing at all");
        AlertsNaming(orderId).Should().BeEmpty("and alerts nothing");
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().BeEmpty("and meters nothing");

        // Scoped to the opt-out warning rather than "any warning from this category": the sweep also warns on a
        // transient fault mid-pass, and a red here must mean the opt-out was silent or repeated, nothing else.
        _factory.Logs.Records
            .Where(entry => entry.Category == typeof(ReconcileSweepHost).FullName
                && entry.Level == LogLevel.Warning
                && entry.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle(
                "a disabled safety detector is a deliberate configured choice, announced once and loudly at startup — never a silent omission");

        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Taking, "the strand simply sits there — which is why disabling is loud");
    }

    [Fact]
    public async Task Sweep_ShouldNeverDetectAStrand_WhenNoSweepIsRunning()
    {
        // PROVE-RED (gh#923). Pre-#722 there was no runtime detector at all: a strand that formed while the process
        // was up was found only by the operator's eye or the next restart's rehydrator. This pins that world — the
        // same strand, the same log and meter channels, no sweep — and then starts the gh#722 host over the SAME
        // strand to show the channels were live all along. The passive half fails the moment anything detects
        // runtime strands without the sweep; the active half fails the moment the sweep stops detecting them.
        (Guid accountId, Guid owner) = await FreshSurfaceAsync();
        Guid orderId = await SeedTakingOrderAsync(accountId, owner);
        ReconcileStrandKey key = new(ReconcileStrandKind.OrderTaking, owner, orderId);

        await Task.Delay(_inertWindow); // many multiples of the age bound the running sweep uses below

        Register.Snapshot().Should().NotContainKey(key, "with no sweep, a runtime strand is never even observed");
        AlertsNaming(orderId).Should().BeEmpty("with no sweep, a runtime strand raises no alert — the pre-#722 gap");
        StrandMeasurements(ExecutionMetrics.ReconcileStrandOrderTaking).Should().BeEmpty("and nothing is metered");

        await using (SweepRun run = await StartSweepAsync(AgedBoundSeconds))
        {
            (await WaitUntilAsync(() => AlertsNaming(orderId).Count >= 1)).Should().BeTrue(
                "the only thing that changed is the gh#722 sweep running — so the silence above was the gap, not a dead channel");
        }

        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Taking, "detected at last, and still untouched");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Driving a pass.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a sweep built the way the composition root builds it — root provider, the singleton register, the
    /// production <see cref="IExecutionMetrics"/> seam (decorator included) and bound options — and hands back a
    /// handle that stops it. One-second cadence, so passes are frequent and the age bound is reachable in-test.
    /// </summary>
    /// <param name="ageBoundSeconds">How long a strand must persist before it is alertable.</param>
    /// <returns>The running sweep; disposing it stops the host.</returns>
    private async Task<SweepRun> StartSweepAsync(int ageBoundSeconds)
    {
        IServiceProvider services = _factory.Services;
        ReconcileSweepHost host = new(
            services,
            services.GetRequiredService<ReconcileStrandRegister>(),
            services.GetRequiredService<IExecutionMetrics>(),
            Options.Create(new ReconcileSweepOptions
            {
                Enabled = true,
                SweepIntervalSeconds = 1,
                StrandAgeThresholdSeconds = ageBoundSeconds,
            }),
            services.GetRequiredService<ILogger<ReconcileSweepHost>>());

        await host.StartAsync(CancellationToken.None);
        return new SweepRun(host);
    }

    /// <summary>A started sweep, stopped and disposed when the test's scope ends.</summary>
    private sealed class SweepRun(ReconcileSweepHost host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    /// <summary>Polls for an outcome rather than sleeping a fixed span, so a slow runner cannot flake the wait.</summary>
    /// <param name="condition">The outcome being waited on.</param>
    /// <returns><c>true</c> if it arrived inside <see cref="_patience"/>.</returns>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Observation.
    // ---------------------------------------------------------------------------------------------------------

    private ReconcileStrandRegister Register => _factory.Services.GetRequiredService<ReconcileStrandRegister>();

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>The sweep's operator alerts that name one entity — scoped by id, so tests never read each other's.</summary>
    /// <param name="entityId">The stranded order's or conditional's id.</param>
    /// <returns>The matching captured records.</returns>
    private IReadOnlyList<CapturedLog> AlertsNaming(Guid entityId) =>
        [.. _factory.Logs.Records.Where(entry =>
            entry.Category == typeof(ReconcileSweepHost).FullName
            && entry.Level == LogLevel.Critical
            && entry.Message.Contains(entityId.ToString(), StringComparison.Ordinal))];

    /// <summary>The strand-detected measurements for one kind tag, since the last reset.</summary>
    /// <param name="kind">The kind tag (<c>order-taking</c> / <c>conditional-firing</c>).</param>
    /// <returns>The matching measurements.</returns>
    private IReadOnlyList<SliCapture.Measurement> StrandMeasurements(string kind) =>
        [.. _factory.Capture.For(ExecutionMetrics.ReconcileStrandsDetected)
            .Where(measurement => measurement.Tags.TryGetValue("kind", out string? tag)
                && string.Equals(tag, kind, StringComparison.Ordinal))];

    // ---------------------------------------------------------------------------------------------------------
    // Seeding.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Clears the decision surface and the metric capture, then returns the bootstrap operator's account. The rows
    /// go first because a leftover <c>Taking</c> row from an earlier case would be discovered as a strand of this
    /// one; the counter is per-kind and undimensioned by entity, so it is counted per test.
    /// </summary>
    /// <returns>The account to seed strands against, and its owner.</returns>
    private async Task<(Guid AccountId, Guid Owner)> FreshSurfaceAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.ConditionalOrders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        _factory.Capture.Clear();

        HttpClient client = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
        Guid accountId = await SetupAccountAsync(client, $"Topstep-Strand-{Guid.NewGuid():N}");
        return (accountId, await OperatorUserIdAsync());
    }

    private Task<Guid> SeedTakingOrderAsync(Guid accountId, Guid owner) => SeedOrderAsync(accountId, owner, OrderStatus.Taking);

    private async Task<Guid> SeedOrderAsync(Guid accountId, Guid owner, OrderStatus status)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = Entry,
                WorkingStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ReferencePrice = Entry,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = status,
                Mode = TradingMode.Practice,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private async Task<Guid> SeedFiringConditionalAsync(Guid accountId, Guid owner)
    {
        Guid conditionalId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.ConditionalOrders.Add(new ConditionalOrderRecord
            {
                Id = conditionalId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = Entry,
                WorkingStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ReferencePrice = Entry,
                TickSize = 0.25m,
                PointValue = 5m,
                TriggerPrice = 5_010m,
                TriggerDirection = ConditionalCrossDirection.RisesTo,
                Status = ConditionalStatus.Firing,
                Mode = TradingMode.Practice,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return conditionalId;
    }

    private Task SetOrderStatusAsync(Guid orderId, OrderStatus status) => ExecuteDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(update => update.SetProperty(order => order.Status, status)));

    // ---------------------------------------------------------------------------------------------------------
    // Fixtures over the public API.
    // ---------------------------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthenticatedClientAsync(string email, string password)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Creates a second operator through the invitation flow — a genuinely different owner for the R-20 case.</summary>
    /// <returns>An authenticated client for the new operator, and its user id.</returns>
    private async Task<(HttpClient Client, Guid UserId)> CreateSecondOperatorAsync()
    {
        string email = $"strand-owner-{Guid.NewGuid():N}@example.com";
        const string password = "OperatorB-Pass123!";

        HttpClient bootstrap = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
        using HttpResponseMessage issue = await bootstrap.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.EnsureSuccessStatusCode();
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, password, "Strand Owner"));
        accept.EnsureSuccessStatusCode();
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        Guid userId = await QueryDbAsync(db =>
            db.Users.IgnoreQueryFilters().Where(user => user.Email == email).Select(user => user.Id).SingleAsync());
        return (client, userId);
    }

    /// <summary>Firm → practice conventions → connection → discovered accounts, as whichever operator owns the client.</summary>
    /// <param name="client">The operator's authenticated client.</param>
    /// <param name="firmName">A unique firm name.</param>
    /// <returns>A tradeable account id.</returns>
    private async Task<Guid> SetupAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        createFirm.EnsureSuccessStatusCode();
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        createConn.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Database reads.
    // ---------------------------------------------------------------------------------------------------------

    private Task<Guid> OperatorUserIdAsync() => QueryDbAsync(db => db.Users
        .IgnoreQueryFilters().Where(user => user.Email == PostgresApiFactory.OperatorEmail).Select(user => user.Id).SingleAsync());

    private Task<OrderStatus> OrderStatusAsync(Guid orderId) => QueryDbAsync(db => db.Orders
        .IgnoreQueryFilters().Where(order => order.Id == orderId).Select(order => order.Status).SingleAsync());

    private Task<string?> OrderVenueKeyAsync(Guid orderId) => QueryDbAsync(db => db.Orders
        .IgnoreQueryFilters().Where(order => order.Id == orderId).Select(order => order.VenueOrderKey).SingleAsync());

    private Task<ConditionalStatus> ConditionalStatusAsync(Guid id) => QueryDbAsync(db => db.ConditionalOrders
        .IgnoreQueryFilters().Where(conditional => conditional.Id == id).Select(conditional => conditional.Status).SingleAsync());

    private Task<long> OrderCountAsync() => QueryDbAsync(db => db.Orders.IgnoreQueryFilters().LongCountAsync());

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
