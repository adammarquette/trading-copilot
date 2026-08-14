using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Pre-merge integration coverage for the <b>immutable §9 audit trail</b> that gh#765 wired at the two
/// safety-critical write sites — every kill-switch engage/disengage and every auto-flatten — driven <b>end to end
/// through the real host</b> against real Postgres (gh#789 ⇒ gh#765; engineering §9, R-11, R-13, ADR-0007,
/// ADR-0013). The unit tier proves each write site stamps the right fields against a fake <c>IAuditLog</c>; what
/// only the real composition + a real database witness is that the rows are <b>durable</b>, that the kill-switch
/// history <b>survives the re-engage</b> that overwrites the single mutable <c>KillSwitchState</c> row, that a
/// background flatten stamps the <b>account's</b> owner with no ambient operator, and that the whole outcome set
/// (<c>Flat</c> / <c>Escalated</c> / <c>Missed</c>) reaches the table intact.
/// </summary>
/// <remarks>
/// The one seam doubled is the venue (<see cref="AdversarialTestProjectXVenueFactory"/>, the QA-sanctioned venue
/// stub): it seeds the open position and, for the escalation, keeps reporting it open. The kill switch is driven over
/// HTTP (hold-to-confirm); the auto-flatten through the clock-free <see cref="AutoFlattenService.RunPassAsync"/>
/// at a controlled instant, exactly as <see cref="AutoFlattenSchedulerIntegrationTests"/> and
/// <see cref="KillSwitchIntegrationTests"/> do. The kill switch is a deployment-wide singleton, so the engaging test
/// disengages in a <c>finally</c>. The DB-level check constraints and the R-20 filter are proven separately in
/// <see cref="MarqSpec.TradingCopilot.IntegrationTests.Data.AuditRecordConstraintIntegrationTests"/>.
/// </remarks>
public class AuditTrailIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Summer date → CDT (UTC−5): ES closes 14:30 CT = 19:30 UTC; the firing window is one hour.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private sealed record LoginTokenResponse(string Token);

    public AuditTrailIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // Kill switch — the history survives the re-engage that overwrites the mutable state row (gh#765 acceptance 1 + 3).
    // =============================================================================================================

    [Fact]
    public async Task EngageDisengageEngage_ShouldLeaveThreeImmutableOrderedRows_ThatSurviveTheReEngage()
    {
        HttpClient client = await AuthenticatedClientAsync();
        await EnsureDisengagedAsync(client);
        await ClearAuditAsync();

        try
        {
            await EngageAsync(client, "first engage");

            // Capture the first engage's row now, so after the whole sequence we can prove the re-engage did NOT
            // rewrite it — the append-only invariant a mutable projection could not offer.
            AuditRecord firstEngage = await SingleKillRowAsync();

            await DisengageAsync(client);
            await EngageAsync(client, "second engage");

            List<AuditRecord> rows = await KillRowsAsync();
            rows.Select(row => row.Action).Should().Equal(
                [AuditAction.KillSwitchEngaged, AuditAction.KillSwitchDisengaged, AuditAction.KillSwitchEngaged],
                "the immutable trail keeps all three transitions in order — the re-engage APPENDS a row, it does not "
                + "overwrite the single mutable KillSwitchState row the way a re-engage used to (gh#765)");
            rows.Should().OnlyContain(
                row => row.Placement == AuditPlacement.None,
                "a kill is an account/system action resting on no single protective leg");
            rows.Should().OnlyContain(
                row => row.Source == AuditSource.Operator, "the operator tripped every one of these transitions");
            rows.Select(row => row.UserId).Distinct().Should().ContainSingle(
                "every row is owned by the one operator who engaged it (R-20)")
                .Which.Should().NotBe(Guid.Empty, "the owner is the authenticated operator, never the empty default");

            AuditRecord firstReRead = await ByIdAsync(firstEngage.Id);
            firstReRead.After.Should().Be(
                firstEngage.After, "the first engage row is immutable — the re-engage appended a new row, it did not "
                + "rewrite this one");
            firstReRead.Detail.Should().Be(firstEngage.Detail, "…and its detail (carrying 'first engage') is untouched");
            firstReRead.RecordedAt.Should().Be(firstEngage.RecordedAt, "…and its timestamp is the original, not the re-engage's");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    // =============================================================================================================
    // Auto-flatten — the outcome set reaches the table, owned by the ACCOUNT's owner (gh#765 acceptance 2 + outcomes).
    // =============================================================================================================

    [Fact]
    public async Task AutoFlatten_ShouldDurablyRecordFlat_OwnedByTheAccountsOwner_WithTheInstrumentInDetail()
    {
        string venueKey = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);
        await ClearAuditAsync();

        await RunPassAsync(Utc(19, 45)); // 14:45 CT — past the 14:30 ES deadline, inside the firing window

        AuditRecord row = await SingleAutoFlattenRowAsync();
        row.After.Should().Be("Flat", "the open ES position closed at its deadline");
        row.Source.Should().Be(AuditSource.Scheduler, "the auto-flatten scheduler tripped it — not an operator");
        row.Placement.Should().Be(AuditPlacement.None, "a flatten acts on the position, not a single protective leg");
        row.Detail.Should().Contain("ES", "the row records WHAT it closed, so the exposure is reconstructable from the trail alone");

        Guid accountOwner = await AccountOwnerAsync(venueKey);
        accountOwner.Should().NotBe(Guid.Empty, "the seeded account has a real owner");
        row.UserId.Should().Be(
            accountOwner, "a background flatten has no ambient operator (ICurrentUser) — the row is owned by the "
            + "ACCOUNT's owner, looked up from the venue account key, exactly as R-20 requires");
    }

    [Fact]
    public async Task AutoFlatten_ShouldDurablyRecordEscalated_WhenTheVenueWillNotConfirmFlat()
    {
        string venueKey = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);
        VenueFactory.MakeCloseIneffective("ESM25"); // the venue keeps reporting it open past the attempt cap
        await ClearAuditAsync();

        await RunPassAsync(Utc(19, 45));

        AuditRecord row = await SingleAutoFlattenRowAsync();
        row.After.Should().Be(
            "Escalated", "a position surviving the attempt cap records the still-open escalation — the incident-worthy "
            + "outcome the immutable trail must hold, never a false Flat");
        row.Source.Should().Be(AuditSource.Scheduler);
        row.Placement.Should().Be(AuditPlacement.None);
    }

    [Fact]
    public async Task AutoFlatten_ShouldDurablyRecordMissed_WhenTheFiringWindowHasPassed()
    {
        string venueKey = await FreshAccountWithKeyAsync();
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);
        await ClearAuditAsync();

        await RunPassAsync(Utc(21, 0)); // 16:00 CT — 90 min past 14:30, beyond the one-hour firing window

        AuditRecord row = await SingleAutoFlattenRowAsync();
        row.After.Should().Be(
            "Missed", "a deadline blown past the firing window is recorded, never silent — firing blind into the "
            + "settlement window is worse than escalating (ADR-0013)");
        row.Source.Should().Be(AuditSource.Scheduler);
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task EngageAsync(HttpClient client, string reason)
    {
        using HttpResponseMessage engage = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: true, Reason: reason));
        engage.StatusCode.Should().Be(HttpStatusCode.OK, "the hold-to-confirm engage succeeds");
    }

    private async Task DisengageAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/kill-switch/disengage", content: null);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureDisengagedAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync("/kill-switch");
        KillSwitchStateResponse? current = await state.Content.ReadFromJsonAsync<KillSwitchStateResponse>(_jsonOptions);
        if (current?.Engaged == true)
        {
            await DisengageAsync(client);
        }
    }

    private async Task RunPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    /// <summary>Clears seeded positions + every account row, then stands up one tradeable account and returns its
    /// venue key — so the process-global <c>RunPassAsync</c> acts on exactly this test's account.</summary>
    private async Task<string> FreshAccountWithKeyAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Audit-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        return accounts.First(account => account.CanTrade).VenueAccountKey;
    }

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

    private Task<List<AuditRecord>> KillRowsAsync() => QueryDbAsync(db => db.AuditRecords
        .IgnoreQueryFilters()
        .Where(row => row.Action == AuditAction.KillSwitchEngaged || row.Action == AuditAction.KillSwitchDisengaged)
        .OrderBy(row => row.RecordedAt)
        .ThenBy(row => row.Id)
        .ToListAsync());

    private async Task<AuditRecord> SingleKillRowAsync() => (await KillRowsAsync()).Should().ContainSingle(
        "exactly one kill-switch transition has been recorded at this point").Which;

    private Task<AuditRecord> SingleAutoFlattenRowAsync() => QueryDbAsync(async db =>
    {
        List<AuditRecord> rows = await db.AuditRecords.IgnoreQueryFilters()
            .Where(row => row.Action == AuditAction.AutoFlatten)
            .ToListAsync();
        return rows.Should().ContainSingle("this pass flattened exactly one seeded account").Which;
    });

    private Task<AuditRecord> ByIdAsync(Guid id) =>
        QueryDbAsync(db => db.AuditRecords.IgnoreQueryFilters().SingleAsync(row => row.Id == id));

    private Task<Guid> AccountOwnerAsync(string venueAccountKey) => QueryDbAsync(db => db.Accounts
        .IgnoreQueryFilters()
        .Where(account => account.VenueAccountKey == venueAccountKey)
        .Select(account => account.UserId)
        .FirstAsync());

    private Task ClearAuditAsync() => ExecuteDbAsync(db => db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync());

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
