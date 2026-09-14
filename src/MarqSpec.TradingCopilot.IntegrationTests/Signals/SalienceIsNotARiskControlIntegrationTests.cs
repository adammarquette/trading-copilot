using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Signals;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Signals;

/// <summary>
/// Independent QA for gh#362 Part B — the safety invariant gh#27 / ADR-0014 exist to guard: importance feedback is
/// a <b>soft salience weight, never a risk control</b>. Extended by gh#971 to the gh#762 <b>direction</b> axis
/// (👍/👎): direction is structurally unreachable from enforcement too (ADR-0007), so it must hold the same
/// byte-identical property. Exercises the <b>real order-submit → gate path</b> (<c>POST /accounts/{id}/orders</c> →
/// <c>OrderEndpoints.ComposeAsync</c>/<c>BuildRequestAsync</c> →
/// <see cref="MarqSpec.TradingCopilot.Domain.Execution.OrderExecutionService"/> → <see cref="RiskGate"/>), with the
/// adversarial venue stub — and asserts that a maximally-starred, maximally-muted, maximally-thumbs-up, or
/// maximally-thumbs-down operator profile leaves the gate's decision <b>byte-identical</b> to the un-rated baseline.
/// </summary>
/// <remarks>
/// <para>
/// This is the behavioural half of a two-tier guard. The dev-side unit test
/// <c>UnitTests.Domain.Signals.SalienceIsNotARiskControlTests</c> asserts <b>structurally</b> that no
/// <c>Domain.Risk</c> type references a <c>Domain.Signals</c> type on its member surface — but that guard is
/// explicit that it cannot see (1) a risk-path method <b>body</b> reading <c>SoftSignalFeedbacks</c> through an
/// existing <c>DbContext</c> field, or (2) the gate <b>call site</b> in <c>Api/Orders/OrderEndpoints.cs</c> —
/// e.g. scaling <c>OrderProposal.RequestedQuantity</c> (a plain <c>int</c>) by a salience multiplier before the
/// proposal is built, which is untyped and so invisible to any reflection-based check. This suite is that closure:
/// it seeds real <see cref="SoftSignalFeedback"/> rows for the authenticated operator directly (no matching
/// <c>NewsRecord</c> is required — the entity carries no FK to it) and diffs the gate's real decision with and
/// without them in play. <see cref="AssertGateDecisionIsInvariantToFeedbackAsync"/> and
/// <see cref="SeedMaximalFeedbackAsync"/> take the <see cref="SoftSignalKind"/> as a plain parameter — kind-agnostic
/// by construction — so gh#971's <c>ThumbsUp</c>/<c>ThumbsDown</c> Theories below reuse the identical mechanism
/// that already closes the leak for Star/Mute; a defect that fed ANY <see cref="SoftSignalFeedback"/> row into the
/// gate regardless of kind would redden all four Theories together, not just two of them.
/// </para>
/// <para>
/// Two request sizes are exercised per feedback direction: a small quantity (1) the baseline gate <b>Allows</b>
/// outright, and a deliberately oversized one the baseline gate must <b>Resize</b>. A multiplicative
/// quantity-scaling leak is most visible on the <i>small</i> request — a 5× star multiplier turns an
/// allowed-at-1 into a resized-at-fewer-than-5 — while a leak that only changes the <b>requested</b> quantity fed
/// to an already-oversized ask can hide behind an unchanged <c>ApprovedQuantity</c> (the layer caps it either
/// way) and surface only in the persisted <see cref="GateDecision.Reason"/> text, which embeds the requested
/// figure. Testing both closes that gap.
/// </para>
/// <para>
/// Part A — the salience ranking itself (star raises, mute lowers, the floor, persistence) — is
/// <see cref="NewsSalienceIntegrationTests"/>, a separate file because it drives a completely different seam
/// (the <c>/api/news</c> read) than the order-gate path exercised here.
/// </para>
/// </remarks>
public sealed class SalienceIsNotARiskControlIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SalienceIsNotARiskControlIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
        // The bootstrap operator is shared across every test in this class (one Postgres container per class) --
        // start each test from a feedback-free operator, so an earlier case's stars/mutes can never leak into a
        // later case's "un-rated baseline".
        ResetFeedbackAsync().GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(1)] // the baseline gate ALLOWS this outright -- most sensitive to a MULTIPLICATIVE leak
    [InlineData(100)] // the baseline gate must RESIZE this (oversized) -- proves the invariant on a resize too
    public async Task SendOrder_ShouldProduceAByteIdenticalGateDecision_WhenTheOperatorHasMaximallyStarredSignals(int quantity)
    {
        await AssertGateDecisionIsInvariantToFeedbackAsync(quantity, SoftSignalKind.Star);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task SendOrder_ShouldProduceAByteIdenticalGateDecision_WhenTheOperatorHasMaximallyMutedSignals(int quantity)
    {
        await AssertGateDecisionIsInvariantToFeedbackAsync(quantity, SoftSignalKind.Mute);
    }

    // --- gh#971: the gh#762 DIRECTION axis (👍/👎) must hold the identical invariant -- same mechanism, other kinds ---

    [Theory]
    [InlineData(1)] // the baseline gate ALLOWS this outright -- most sensitive to a MULTIPLICATIVE leak
    [InlineData(100)] // the baseline gate must RESIZE this (oversized) -- proves the invariant on a resize too
    public async Task SendOrder_ShouldProduceAByteIdenticalGateDecision_WhenTheOperatorHasMaximallyThumbsUpSignals(int quantity)
    {
        await AssertGateDecisionIsInvariantToFeedbackAsync(quantity, SoftSignalKind.ThumbsUp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task SendOrder_ShouldProduceAByteIdenticalGateDecision_WhenTheOperatorHasMaximallyThumbsDownSignals(int quantity)
    {
        await AssertGateDecisionIsInvariantToFeedbackAsync(quantity, SoftSignalKind.ThumbsDown);
    }

    private async Task AssertGateDecisionIsInvariantToFeedbackAsync(int quantity, SoftSignalKind kind)
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid baselineAccountId = await SetupAccountAsync(client, $"Signals-Baseline-{kind}-{quantity}");
        await DeclareRiskProfileAsync(client, baselineAccountId);
        Guid treatmentAccountId = await SetupAccountAsync(client, $"Signals-{kind}-{quantity}");
        await DeclareRiskProfileAsync(client, treatmentAccountId);

        // Same order, same risk profile, same everything -- the ONLY thing that will differ between the two
        // sends is whether the operator's SoftSignalFeedbacks exist. The instrument (MES) matches what the order
        // itself trades -- the worst case for a leak keyed on the order's own instrument.
        SendOrderRequest request = ValidOrderRequest(quantity);

        using HttpResponseMessage baselineResponse = await client.PostAsJsonAsync($"/accounts/{baselineAccountId}/orders", request);
        SendOrderResponse? baseline = await baselineResponse.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(baseline);

        // A MAXIMALLY starred/muted profile for the operator: six distinct feedback rows all sharing the order's
        // own instrument, stacking the salience model's weight past its own clamp (cap 5x / floor 0.25x) -- as
        // extreme a signal as the model can produce, "in play for the operator" exactly as the gh#27 acceptance
        // criterion names it.
        await SeedMaximalFeedbackAsync(kind, instrument: "MES");

        using HttpResponseMessage treatmentResponse = await client.PostAsJsonAsync($"/accounts/{treatmentAccountId}/orders", request);
        SendOrderResponse? treatment = await treatmentResponse.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(treatment);

        // THE assertion gh#362 exists to make: the gate's decision is byte-identical, feedback or not.
        treatmentResponse.StatusCode.Should().Be(baselineResponse.StatusCode,
            "a soft salience weight must never change which HTTP outcome the gate produces");
        treatment.Outcome.Should().Be(baseline.Outcome);
        treatment.ApprovedQuantity.Should().Be(baseline.ApprovedQuantity);
        treatment.BindingLayer.Should().Be(baseline.BindingLayer);
        treatment.Reason.Should().Be(baseline.Reason,
            "the reason text embeds the requested quantity on a resize -- a leak that only scales the REQUESTED "
            + "quantity before the gate runs would still surface here even when ApprovedQuantity does not move");
        treatment.Advisories.Should().BeEquivalentTo(baseline.Advisories);

        // And the durable audit trail -- the GateDecisionRecord a leak would also have to falsify -- agrees.
        await ExecuteDbContextAsync(async database =>
        {
            GateDecisionRecord baselineDecision = await database.GateDecisions.IgnoreQueryFilters()
                .SingleAsync(decision => decision.AccountId == baselineAccountId);
            GateDecisionRecord treatmentDecision = await database.GateDecisions.IgnoreQueryFilters()
                .SingleAsync(decision => decision.AccountId == treatmentAccountId);

            treatmentDecision.Outcome.Should().Be(baselineDecision.Outcome);
            treatmentDecision.ApprovedQuantity.Should().Be(baselineDecision.ApprovedQuantity);
            treatmentDecision.BindingLayer.Should().Be(baselineDecision.BindingLayer);
            treatmentDecision.Reason.Should().Be(baselineDecision.Reason);
        });
    }

    // --- Helpers ---

    // Six distinct feedback rows all sharing ONE instrument. For Star/Mute (Importance) this stacks the operator's
    // profile weight for (Instrument, instrument) well past the scorer's own clamp in either direction; for
    // ThumbsUp/ThumbsDown (Direction, gh#971) SalienceProfile.Build skips them entirely by construction, so the six
    // rows contribute ZERO weight -- as extreme a signal as either axis's model can produce, "in play for the
    // operator" exactly as the gh#27 acceptance criterion (and gh#762's extension of it) names it. No matching
    // NewsRecord is seeded: SoftSignalFeedback carries no FK to it (by NewsDedupKey, decoupled by design), and this
    // suite's subject -- the order path -- never joins through the news store at all.
    private Task SeedMaximalFeedbackAsync(SoftSignalKind kind, string instrument) =>
        ExecuteDbContextAsync(async database =>
        {
            User operatorUser = await database.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 6; i++)
            {
                database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
                {
                    Id = Guid.NewGuid(),
                    UserId = operatorUser.Id,
                    NewsDedupKey = $"https://example.com/signals-safety-{instrument}-{kind}-{i}",
                    Kind = kind,
                    CreatedAt = now,
                });
            }

            await database.SaveChangesAsync();
        });

    private Task ResetFeedbackAsync() =>
        ExecuteDbContextAsync(database => database.SoftSignalFeedbacks.IgnoreQueryFilters().ExecuteDeleteAsync());

    private async Task<Guid> SetupAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First().Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest declareReq = new(
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
            StartingBalance: 50_000m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private static SendOrderRequest ValidOrderRequest(int quantity)
    {
        return new SendOrderRequest(
            Symbol: "MES",
            TickSize: 0.25m,
            PointValue: 5m,
            Side: OrderSide.Buy,
            Quantity: quantity,
            Entry: 5_000m,
            Stop: 4_990m,
            SafetyStop: 4_980m,
            ReferencePrice: 5_000m,
            Type: OrderType.Market);
    }

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }
}
