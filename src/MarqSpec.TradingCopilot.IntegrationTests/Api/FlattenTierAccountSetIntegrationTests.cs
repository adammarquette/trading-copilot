using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Which accounts each flatten tier acts on</b> (gh#512 ⇒ gh#187, R-13, ADR-0013, ADR-0015) — the credential-set
/// boundary, and the agreement between the primary tier and the watchdog that backstops it.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0015 gives a process <b>one credential set</b>: an account reached through a connection on another key
/// belongs to a sibling process and is not ours to act on. Both flatten tiers implement that skip independently,
/// and it must hold on both — a tier that flattened someone else's account would close a position its own operator
/// never opened.
/// </para>
/// <para>
/// <b>Why this suite exists.</b> Both tiers had a unit guard for the skip, and <b>neither could fail</b>. Their
/// shared <c>Venue(...)</c> helper stubs <c>Id</c>, <c>GetPositionsAsync</c>, <c>ResolveContractAsync</c> and
/// <c>ClosePositionAsync</c> but <b>not <c>GetAccountsAsync</c></b>, so the venue roster is FakeItEasy's empty
/// dummy. Every account then fails the <c>roster.FirstOrDefault(...)</c> match and is skipped as "not reported by
/// the venue" long before the credential comparison is reached — so <c>ClosePositionAsync.MustNotHaveHappened()</c>
/// is satisfied by the empty roster, whatever the credential filter does. Deleting the filter outright leaves both
/// unit tests green.
/// </para>
/// <para>
/// <b>Asserted before any venue read, not merely "no close".</b> The credential check sits <i>above</i>
/// <c>GetAccountsAsync</c>, so the observable guarantee is that a foreign account produces <b>no venue traffic at
/// all</b>. <c>PositionReadCount</c> witnesses that; "no close happened" cannot, because it is equally true of a
/// pass that read the venue and then declined to act.
/// </para>
/// </remarks>
public class FlattenTierAccountSetIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public FlattenTierAccountSetIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The credential set this process holds — <c>StubbedVenuePostgresFactory</c> sets it.</summary>
    private const string OurCredentialKey = "topstep-main";

    /// <summary>A sibling process's credential set. Same venue, same database, not ours to act on.</summary>
    private const string SiblingCredentialKey = "another-firm";

    // 14:45 CT — past the 14:30 ES deadline and well beyond the watchdog's grace, so BOTH tiers are due to act.
    private static DateTimeOffset PastTheDeadline { get; } = new(2026, 7, 15, 19, 45, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunPass_ShouldSkipAnAccountOnASiblingCredentialKey_BeforeAnyVenueRead()
    {
        IReadOnlyList<string> accounts = await FreshAccountsAsync(SiblingCredentialKey);
        SeedOpenPositions(accounts);

        await RunPrimaryAsync(PastTheDeadline);

        VenueFactory.PositionReadCount.Should().Be(0,
            "the credential check sits above GetAccountsAsync, so an account belonging to a sibling process must "
            + "produce no venue traffic whatsoever (ADR-0015) — not merely no close");
        VenueFactory.ClosePositionCalls.Should().BeEmpty(
            "and nothing of a sibling process's may be closed");
    }

    [Fact]
    public async Task Watchdog_ShouldSkipAnAccountOnASiblingCredentialKey_BeforeAnyVenueRead()
    {
        IReadOnlyList<string> accounts = await FreshAccountsAsync(SiblingCredentialKey);
        SeedOpenPositions(accounts);

        await RunWatchdogAsync(PastTheDeadline);

        VenueFactory.PositionReadCount.Should().Be(0,
            "the backstop carries the same boundary as the tier it backstops; a watchdog that reached past the "
            + "credential set would be a second, unsupervised path to closing someone else's position");
        VenueFactory.ClosePositionCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task BothTiers_ShouldActOnTheSameAccountSet_ForTheSameDatabaseAndCredentialKey()
    {
        // Behavioural rather than structural: the two tiers resolve their account set through separate code, and a
        // backstop that silently covers a DIFFERENT set than the tier it backstops is the failure ADR-0013's
        // redundancy exists to prevent — an account the primary misses would then be missed twice.
        IReadOnlyList<string> accounts = await FreshAccountsAsync(OurCredentialKey);

        SeedOpenPositions(accounts);
        await RunPrimaryAsync(PastTheDeadline);
        string[] primaryActedOn = ClosedAccountKeys();

        // Re-arm: the primary's closes left the venue flat, so the watchdog would otherwise find nothing to do.
        VenueFactory.ResetPositions();
        SeedOpenPositions(accounts);
        await RunWatchdogAsync(PastTheDeadline);
        string[] watchdogActedOn = ClosedAccountKeys();

        primaryActedOn.Should().NotBeEmpty(
            "the positive control — if the primary acted on nothing, the comparison below is vacuous and would "
            + "hold for two tiers that both do nothing at all");
        watchdogActedOn.Should().BeEquivalentTo(primaryActedOn,
            "both tiers read the same database through the same credential set, so they must cover the same "
            + "accounts; a divergence means the backstop is not backstopping what it appears to");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private string[] ClosedAccountKeys() =>
        [.. VenueFactory.ClosePositionCalls.Select(call => call.AccountKey).Distinct().Order()];

    private void SeedOpenPositions(IReadOnlyList<string> accountKeys)
    {
        foreach (string accountKey in accountKeys)
        {
            VenueFactory.SeedPosition(accountKey, "ESM25", netQuantity: 2);
        }
    }

    private async Task RunPrimaryAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    private async Task RunWatchdogAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenWatchdogService service = scope.ServiceProvider.GetRequiredService<AutoFlattenWatchdogService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    /// <summary>
    /// Clears every account row and discovers a fresh set, optionally <b>reassigning</b> the connection to another
    /// credential set afterwards. Returns the tradeable venue account keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reassignment is a database write rather than a differently-keyed discovery, because discovery
    /// <b>refuses</b> a connection on another key — <c>POST /connections/{id}/accounts/discover</c> answers
    /// <c>409 Conflict</c>. That guard is correct and worth knowing, but it means the API cannot produce the state
    /// under test here.
    /// </para>
    /// <para>
    /// It is still a state the flatten tiers must survive, and the only way it actually arises: rows discovered
    /// under one credential set, whose connection now belongs to a sibling process — after a credential rotation
    /// (gh#210), or with two processes against one database, which is precisely what ADR-0015 contemplates.
    /// </para>
    /// <para>
    /// Resetting the venue AFTER discovery matters: the read counter must start at zero for the pass under test,
    /// and discovery legitimately talks to the venue.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> FreshAccountsAsync(string credentialKey)
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-TierSet-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", OurCredentialKey));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        if (!string.Equals(credentialKey, OurCredentialKey, StringComparison.Ordinal))
        {
            await ExecuteDbAsync(db => db.Connections
                .IgnoreQueryFilters()
                .Where(candidate => candidate.Id == connection.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.CredentialKey, credentialKey)));
        }

        // Discovery is legitimate venue traffic; the counter must measure only the pass under test.
        VenueFactory.ResetPositions();

        return [.. accounts.Where(account => account.CanTrade).Select(account => account.VenueAccountKey)];
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

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private sealed record LoginTokenResponse(string Token);
}
