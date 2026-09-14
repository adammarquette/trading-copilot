using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>dead-man's-switch daily check-in</b> (gh#408 ⇒ gh#244, R-13, ADR-0019) — the
/// <b>tier-3, out-of-process</b> auto-flatten redundancy, whose <b>silence</b> is the pager signal. If the in-process
/// scheduler and its watchdog both fail, the only remaining signal is that this service <b>stops vouching</b>.
/// </summary>
/// <remarks>
/// <para>
/// Both failure directions are dangerous and both are guarded here: vouching <b>"flat"</b> while exposure is still
/// open lets a position ride overnight behind a green light, and withholding on a <b>genuinely flat</b> market pages
/// the operator every clean day until the net gets muted. The load-bearing case is the first.
/// </para>
/// <para>
/// Driven through the real <see cref="FlattenCheckInService.RunPassAsync"/> at a chosen instant — the sole pass,
/// since <see cref="DeadMansSwitchTestPostgresFactory"/> strips <c>DeadMansSwitchHost</c> along with every other
/// always-on host. Venue truth comes from the adversarial stub (it supplies positions, never the decision) and the
/// external monitor from <see cref="RecordingDeadMansSwitch"/> (it records what was vouched, and can refuse).
/// Accounts are created through the real API so their <c>VenueAccountKey</c>s match the venue roster.
/// Traces R-13 · ADR-0019 · ADR-0015.
/// </para>
/// </remarks>
public class DeadMansSwitchCheckInIntegrationTests : IClassFixture<DeadMansSwitchTestPostgresFactory>
{
    private readonly DeadMansSwitchTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public DeadMansSwitchCheckInIntegrationTests(DeadMansSwitchTestPostgresFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The market this suite's guards are asserted against; other configured markets report independently.</summary>
    private static InstrumentId Es => InstrumentId.Parse("ES");

    // 14:45 CT on 2026-07-15 — past the 14:30 CT ES deadline, so a check-in is due.
    private static DateTimeOffset PastDeadline => new(2026, 7, 15, 19, 45, 0, TimeSpan.Zero);

    // 12:00 CT — well before the deadline; nothing is due and no venue work should happen.
    private static DateTimeOffset BeforeDeadline => new(2026, 7, 15, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckIn_ShouldWithholdTheReport_WhenTheVenueStillShowsExposure()
    {
        HttpClient client = await FreshOperatorAsync();
        string accountKey = await SetupAccountAsync(client);
        // Exposure past the deadline is exactly what the monitor exists to page on.
        VenueFactory.SeedPosition(accountKey, "ESM25", netQuantity: 2);

        IReadOnlyDictionary<InstrumentId, DateOnly> reported = await RunPassAsync(PastDeadline);

        reported.Should().NotContainKey(Es, "an instrument with open exposure is never vouched for");
        // Per-instrument, not global: other configured markets are flat and legitimately report — the guard is that
        // THIS market stays silent, which is what makes the monitor page for it.
        Monitor.FlatReports.Should().NotContain(Es,
            "the switch stays SILENT for the exposed market — the monitor's missing check-in is what pages the operator");
    }

    [Fact]
    public async Task CheckIn_ShouldReportFlat_WhenTheVenueIsFlat()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);

        IReadOnlyDictionary<InstrumentId, DateOnly> reported = await RunPassAsync(PastDeadline);

        Monitor.FlatReports.Should().ContainSingle(instrument => instrument == Es,
            "a flat market past its deadline is vouched for exactly once");
        reported.Should().ContainKey(Es, "the recorded check-in is what stops the next pass repeating it");
        reported[Es].Should().Be(new DateOnly(2026, 7, 15), "the check-in is recorded against the MARKET day");
    }

    [Fact]
    public async Task CheckIn_ShouldWithhold_WhenAnyAccountOnTheVenueStillHoldsThePosition()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);
        // A SECOND account on the same venue still holds ES. Exposure aggregates across every account this process
        // serves: one flat account does not make the instrument flat.
        string otherAccountKey = await OtherAccountKeyAsync();
        VenueFactory.SeedPosition(otherAccountKey, "ESM25", netQuantity: 1);

        IReadOnlyDictionary<InstrumentId, DateOnly> reported = await RunPassAsync(PastDeadline);

        Monitor.FlatReports.Should().NotContain(Es,
            "exposure on ANY account withholds the instrument's check-in — a per-account view would vouch for a market that is not flat");
        reported.Should().NotContainKey(Es);
    }

    [Fact]
    public async Task CheckIn_ShouldOnlyReportForThisProcessesCredentialKey()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);
        // ADR-0015: one credential set per process. Re-point the connection at a SIBLING process's key — this
        // process cannot see those accounts' positions, so it must not vouch for them (flat though they look here).
        await ExecuteDbAsync(async db =>
        {
            await db.Connections.IgnoreQueryFilters()
                .ExecuteUpdateAsync(setters => setters.SetProperty(connection => connection.CredentialKey, "another-process-key"));
        });

        IReadOnlyDictionary<InstrumentId, DateOnly> reported = await RunPassAsync(PastDeadline);

        Monitor.FlatReports.Should().BeEmpty(
            "an account on a sibling process's credential key is skipped — vouching for positions this process cannot read would be a guess");
        reported.Should().BeEmpty();
        VenueFactory.PositionReadCount.Should().Be(0, "the skip happens before any venue read for that connection");
    }

    [Fact]
    public async Task CheckIn_ShouldNotRecordARejectedReport_SoTheNextPassRetries()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);
        Monitor.RejectNextFor(Es); // the monitor refuses ES's report — a transient failure, not a verdict

        IReadOnlyDictionary<InstrumentId, DateOnly> first = await RunPassAsync(PastDeadline);

        EsReportCount().Should().Be(1, "the report WAS attempted — it was the monitor that refused it");
        first.Should().NotContainKey(Es,
            "a rejected report is deliberately NOT recorded: recording it would silently cost the day's check-in over one transient failure");

        // The next pass, carrying forward what WAS recorded, must try the unrecorded instrument again (and skip the
        // ones already vouched for today).
        IReadOnlyDictionary<InstrumentId, DateOnly> second = await RunPassAsync(PastDeadline, first);

        EsReportCount().Should().Be(2, "the unrecorded check-in is retried on the following pass");
        second.Should().ContainKey(Es, "the retry succeeded and is recorded, so the day is now vouched for");
    }

    [Fact]
    public async Task CheckIn_ShouldNotTouchTheVenue_WhenNoInstrumentIsDue()
    {
        HttpClient client = await FreshOperatorAsync();
        await SetupAccountAsync(client);

        IReadOnlyDictionary<InstrumentId, DateOnly> reported = await RunPassAsync(BeforeDeadline);

        reported.Should().BeEmpty("nothing is due before the deadline — flat now says nothing about flat then");
        Monitor.FlatReports.Should().BeEmpty("no market may be vouched for before its deadline has passed");
        // The cheap gate is the point: witness that the pass did NO venue work, which an empty result alone cannot
        // show (a withheld pass is equally empty).
        VenueFactory.PositionReadCount.Should().Be(0, "outside the window the pass reads no venue truth at all");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private RecordingDeadMansSwitch Monitor => _factory.Monitor;

    private int EsReportCount() => Monitor.ReportCountFor(Es);

    private async Task<IReadOnlyDictionary<InstrumentId, DateOnly>> RunPassAsync(
        DateTimeOffset now, IReadOnlyDictionary<InstrumentId, DateOnly>? lastCheckedIn = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        FlattenCheckInService service = scope.ServiceProvider.GetRequiredService<FlattenCheckInService>();
        return await service.RunPassAsync(
            now, lastCheckedIn ?? new Dictionary<InstrumentId, DateOnly>(), CancellationToken.None);
    }

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        Monitor.Reset();
        await ExecuteDbAsync(async db =>
        {
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Connections.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Creates the firm + connection and discovers the roster; returns the tradeable account's venue key.</summary>
    private async Task<string> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-DMS-{Guid.NewGuid():N}", FirmType.PropFirm));
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

    /// <summary>A second discovered account on the same connection — the aggregation case's other holder.</summary>
    private Task<string> OtherAccountKeyAsync() => QueryDbAsync(async db =>
    {
        List<string> keys = await db.Accounts.IgnoreQueryFilters()
            .OrderBy(account => account.VenueAccountKey)
            .Select(account => account.VenueAccountKey)
            .ToListAsync();
        return keys[^1];
    });

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
