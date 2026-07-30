using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>confirm-before-live</b> gate (gh#486 ⇒ gh#470, R-4 / R-7, ADR-0008) — the step that
/// makes gh#15's invariant, <i>"plain rules → <b>confirmed</b> deterministic triggers"</i>, structural rather than
/// procedural.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this tier adds over the unit tier.</b> The unit tests prove the evaluation service skips an unconfirmed
/// trigger. They cannot prove the <b>scan</b> does, because the gate lives in the scan's <i>queries</i> — owner
/// discovery and the per-owner load — and a query predicate is only real against a real database. Nor can they see
/// the <c>CHECK</c>, the index, or the migration's backfill, none of which exist outside Postgres.
/// </para>
/// <para>
/// <b>Every negative here is paired with a positive.</b> "Nothing fired" is the easiest assertion in the world to
/// satisfy by accident — a misspelled symbol, an unseeded indicator, a harness that stripped the notifier. So each
/// inertness claim has a twin that changes <b>only the confirmation</b> and observes a real fire: a
/// <c>TriggerFirings</c> row, an outbox row, and a page on the wire. The pair is the guard; either half alone is
/// decoration.
/// </para>
/// <para>
/// Driven through the real <c>TriggerEvaluationService.ScanAsync(now, …)</c> at a fixed instant (it never reads a
/// clock) with hosted services stripped, so the suite's pass is the only one. Indicator readings are seeded as real
/// <c>IndicatorValues</c> rows and read through the production <c>StoredIndicatorSource</c> — never stubbed to hand
/// back a firing.
/// </para>
/// </remarks>
public class TriggerConfirmationGateIntegrationTests : IClassFixture<ConfirmationGateTestPostgresFactory>
{
    private const string Symbol = "ES";
    private const string Indicator = "rsi";
    private const int Period = 14;
    private const int Resolution = 5;
    private const decimal Threshold = 70m;

    /// <summary>Above the threshold — the condition is satisfied.</summary>
    private const decimal Crossed = 75m;

    /// <summary>Below the threshold — the condition is not satisfied.</summary>
    private const decimal Quiet = 40m;

    private static readonly DateTimeOffset _now = new(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly ConfirmationGateTestPostgresFactory _factory;

    public TriggerConfirmationGateIntegrationTests(ConfirmationGateTestPostgresFactory factory) => _factory = factory;

    private sealed record LoginTokenResponse(string Token);

    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    // =================================================================================================================
    // The gate itself — the differential pair. Only Confirmation differs between these two tests.
    // =================================================================================================================

    [Fact]
    public async Task Scan_ShouldFireNothingAtAll_WhenAnEnabledSatisfiedTriggerIsUnconfirmed()
    {
        await ResetAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid triggerId = await SeedTriggerAsync(userId, TriggerConfirmation.Unconfirmed, TriggerArmState.Armed);
        await SeedIndicatorAsync(Crossed);

        int fires = await ScanAsync();

        fires.Should().Be(0, "an unconfirmed trigger is inert — authorship alone must never arm anything");
        (await FiringCountAsync()).Should().Be(0, "no incident was journalled");
        (await OutboxCountAsync()).Should().Be(0, "and nothing was queued to page the operator");

        // The debounce memory is the tell that the scan never even loaded it. Had the gate merely suppressed the
        // notification, the state machine would still have advanced Armed -> Fired and the trigger would be silently
        // spent — armed for nobody, and unable to fire on the NEXT crossing once confirmed.
        (await ArmStateAsync(triggerId)).Should().Be(
            TriggerArmState.Armed, "the debounce is untouched — a gate that consumes the edge is not a gate");
        (await ConfirmationAsync(triggerId)).Should().Be(TriggerConfirmation.Unconfirmed);

        await _factory.DeliverOutboxAsync();
        _factory.Pushover.Sent.Should().BeEmpty("nothing reached the wire either");
    }

    [Fact]
    public async Task Scan_ShouldFireAndPage_WhenTheOnlyDifferenceIsThatItIsConfirmed()
    {
        await ResetAsync();
        Guid userId = await OperatorUserIdAsync();
        // Byte-for-byte the setup above, with ONE field changed. This is what makes the negative above a guard rather
        // than a coincidence: if the fixture were wrong in any other way, this test would fail too.
        Guid triggerId = await SeedTriggerAsync(userId, TriggerConfirmation.Confirmed, TriggerArmState.Armed);
        await SeedIndicatorAsync(Crossed);

        int fires = await ScanAsync();

        fires.Should().Be(1, "a confirmed trigger fires on the arming edge");
        (await FiringCountAsync()).Should().Be(1, "the incident is journalled");
        (await ArmStateAsync(triggerId)).Should().Be(TriggerArmState.Fired, "and the debounce advanced");

        await _factory.DeliverOutboxAsync();
        _factory.Pushover.Sent.Should().ContainSingle("the operator is actually paged through the production chain");
    }

    // =================================================================================================================
    // Confirmation as an act — the endpoint, and what the NEXT scan does.
    // =================================================================================================================

    [Fact]
    public async Task Confirm_ShouldNotFireOnTheAlreadyTrueLevel_ThenFireOnTheNextObservedEdge()
    {
        await ResetAsync();
        Guid userId = await OperatorUserIdAsync();
        // Unseeded is what a freshly authored trigger looks like; the level is ALREADY true when it is confirmed.
        Guid triggerId = await SeedTriggerAsync(userId, TriggerConfirmation.Unconfirmed, TriggerArmState.Unseeded);
        // Readings walk FORWARD through buckets that are all in the past: the source takes the latest BucketStart <= now,
        // so each seeding supersedes the last. A bucket after `now` would rightly be invisible — no look-ahead.
        await SeedIndicatorAsync(Crossed, atOffsetMinutes: -15);
        HttpClient client = await AuthenticatedClientAsync();

        using HttpResponseMessage confirm = await client.PostAsync($"/api/triggers/{triggerId}/confirm", content: null);

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, "the operator may confirm their own trigger");
        (await ConfirmationAsync(triggerId)).Should().Be(TriggerConfirmation.Confirmed);

        // Confirming must not retroactively fire a condition that was already true before anyone looked. It seeds.
        (await ScanAsync()).Should().Be(0, "adopting pre-existing truth is silent — confirmation is not a crossing");
        (await FiringCountAsync()).Should().Be(0);
        (await ArmStateAsync(triggerId)).Should().Be(TriggerArmState.Fired, "the level was seeded, not fired");

        // Down through the threshold: re-arm. Then back up: the first genuinely OBSERVED crossing.
        await SeedIndicatorAsync(Quiet, atOffsetMinutes: -10);
        (await ScanAsync()).Should().Be(0, "falling away re-arms, it does not fire");
        (await ArmStateAsync(triggerId)).Should().Be(TriggerArmState.Armed);

        await SeedIndicatorAsync(Crossed, atOffsetMinutes: -5);
        (await ScanAsync()).Should().Be(1, "THIS is the observed edge, and a confirmed trigger fires on it");
        (await FiringCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_ShouldReturnNotFoundAndChangeNothing_WhenAnotherOperatorAttemptsIt()
    {
        await ResetAsync();
        Guid ownerId = await OperatorUserIdAsync();
        Guid triggerId = await SeedTriggerAsync(ownerId, TriggerConfirmation.Unconfirmed, TriggerArmState.Armed);
        HttpClient owner = await AuthenticatedClientAsync();
        HttpClient intruder = await SecondOperatorClientAsync(owner);

        using HttpResponseMessage attempt = await intruder.PostAsync($"/api/triggers/{triggerId}/confirm", content: null);

        // 404 rather than 403: another operator's trigger does not exist as far as this one is concerned (R-20).
        attempt.StatusCode.Should().Be(HttpStatusCode.NotFound, "a trigger you do not own is not yours to arm");
        (await ConfirmationAsync(triggerId)).Should().Be(
            TriggerConfirmation.Unconfirmed, "and the refusal left the row exactly as it was");

        await SeedIndicatorAsync(Crossed);
        (await ScanAsync()).Should().Be(0, "so it is still inert — the refusal was real, not cosmetic");
    }

    // =================================================================================================================
    // The schema-level backstop. Defense in depth below the API, per the fail-closed-zero convention.
    // =================================================================================================================

    [Fact]
    public async Task Database_ShouldRefuseAnUnknownConfirmationValue_BecauseTheCheckConstraintIsLive()
    {
        await ResetAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid triggerId = await SeedTriggerAsync(userId, TriggerConfirmation.Confirmed, TriggerArmState.Armed);

        Func<Task> writeGarbage = () => ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Triggers\" SET \"Confirmation\" = 2 WHERE \"Id\" = {triggerId}"));

        (await writeGarbage.Should().ThrowAsync<PostgresException>(
            "CK_Triggers_Confirmation_Known is the backstop for anything that bypasses the API"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    // =================================================================================================================
    // Plumbing.
    // =================================================================================================================

    private async Task<int> ScanAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TriggerEvaluationService service = scope.ServiceProvider.GetRequiredService<TriggerEvaluationService>();
        return await service.ScanAsync(_now, CancellationToken.None);
    }

    private async Task ResetAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.TriggerFirings.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Triggers.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.IndicatorValues.ExecuteDeleteAsync();
        });
        await _factory.ClearOutboxAsync();
        _factory.Pushover.Reset();
    }

    private Task<Guid> OperatorUserIdAsync() => QueryDbAsync(db => db.Users
        .Where(user => user.Email == PostgresApiFactory.OperatorEmail)
        .Select(user => user.Id)
        .SingleAsync());

    /// <summary>Seeds a MECHANICAL trigger — no account, no size — differing only in confirmation and arm state.</summary>
    private async Task<Guid> SeedTriggerAsync(Guid userId, TriggerConfirmation confirmation, TriggerArmState armState)
    {
        Guid id = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Triggers.Add(new TriggerRecord
            {
                Id = id,
                UserId = userId,
                Symbol = Symbol,
                Indicator = Indicator,
                Period = Period,
                ResolutionMinutes = Resolution,
                ConditionKind = TriggerConditionKind.IndicatorThreshold,
                Comparison = IndicatorComparison.Above,
                Threshold = Threshold,
                Route = TriggerRoute.Mechanical,
                Severity = NotificationSeverity.Notify,
                Enabled = true,
                Confirmation = confirmation,
                ArmState = armState,
                ArmCycle = 0,
                CreatedAt = _now.AddDays(-1),
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>
    /// Writes the reading the scan will resolve. <paramref name="atOffsetMinutes"/> moves the bucket forward so a
    /// later reading supersedes an earlier one, which is how a crossing is expressed to the production source.
    /// </summary>
    private Task SeedIndicatorAsync(decimal value, int atOffsetMinutes = -Resolution) => ExecuteDbAsync(async db =>
    {
        db.IndicatorValues.Add(new IndicatorValueRecord
        {
            Venue = "projectx",
            Instrument = Symbol,
            ResolutionMinutes = Resolution,
            Indicator = Indicator,
            Period = Period,
            BucketStart = _now.AddMinutes(atOffsetMinutes),
            Value = value,
            RecordedAt = _now.AddMinutes(atOffsetMinutes),
        });
        await db.SaveChangesAsync();
    });

    private Task<int> FiringCountAsync() =>
        QueryDbAsync(db => db.TriggerFirings.IgnoreQueryFilters().CountAsync());

    private Task<int> OutboxCountAsync() =>
        QueryDbAsync(db => db.NotificationOutbox.CountAsync());

    private Task<TriggerArmState> ArmStateAsync(Guid triggerId) => QueryDbAsync(db => db.Triggers
        .IgnoreQueryFilters().Where(trigger => trigger.Id == triggerId).Select(trigger => trigger.ArmState).SingleAsync());

    private Task<TriggerConfirmation> ConfirmationAsync(Guid triggerId) => QueryDbAsync(db => db.Triggers
        .IgnoreQueryFilters().Where(trigger => trigger.Id == triggerId).Select(trigger => trigger.Confirmation).SingleAsync());

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        login.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await login.Content.ReadFromJsonAsync<LoginTokenResponse>(_json);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>A second operator via the real invitation flow — never a forged token.</summary>
    private async Task<HttpClient> SecondOperatorClientAsync(HttpClient operatorClient)
    {
        string email = $"confirm-gate-b-{Guid.NewGuid():N}@example.com";
        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync(
            "/auth/invitations", new IssueInvitationRequest(email));
        issue.EnsureSuccessStatusCode();
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_json);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "ConfirmGateB-Pass123!", "Confirm Gate B"));
        accept.EnsureSuccessStatusCode();
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_json);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }
}
