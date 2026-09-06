using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// R-4's payoff for the snapshot-not-FK decision (gh#958, QA for gh#729; ADR-0026): a suggestion's cited
/// <see cref="CitedFactorKind.Level"/> factor is a <b>copy</b> of the <see cref="PriceLevel"/> zone as it stood
/// at issuance, deliberately carrying no foreign key (<see cref="CitedFactor.LevelId"/> is a soft reference). This
/// suite drives the source <see cref="PriceLevel"/> row through every mutation the real detector performs — a
/// merge, a role flip, a retirement, and finally a delete — and asserts the citation on the suggestion never moves
/// and the suggestion still reads, because there is no FK relationship left to break.
/// </summary>
/// <remarks>
/// Written independently of the gh#729 implementation, from the issue's own acceptance criteria (gh#958). Only a
/// container-backed run can prove the <i>absence</i> of an FK actually holds in the schema Postgres applies — an
/// EF InMemory context enforces no foreign keys at all, so a test against it would pass identically whether or
/// not <c>LevelId</c> carries a real constraint (QA contract §1).
/// </remarks>
public class CitedFactorLevelSnapshotImmutabilityIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private const string Instrument = "ESM25";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public CitedFactorLevelSnapshotImmutabilityIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CitedLevelSnapshot_StaysFixed_ThroughAMergeAFlipARetirementAndFinallyTheSourceLevelsDeletion()
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid levelId = Guid.NewGuid();
        Guid factorId = Guid.NewGuid();
        Guid suggestionId = Guid.NewGuid();

        DateTimeOffset formedAt = DateTimeOffset.UtcNow.AddHours(-6);

        await ExecuteDbAsync(async db =>
        {
            db.PriceLevels.Add(new PriceLevel
            {
                Id = levelId,
                Venue = "TOPSTEPX",
                Instrument = "ES",
                TimeframeMinutes = 60,
                Top = 5_310.00m,
                Bottom = 5_305.00m,
                Kind = PriceLevelKind.Resistance,
                Significance = 8.0m,
                FormedAtBucket = formedAt,
                TouchCount = 3,
                Active = true,
                UpdatedAt = formedAt,
            });

            Suggestion suggestion = new()
            {
                Origin = SuggestionOrigin.Scan,
                Id = suggestionId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = Instrument,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_308m,
                StopPrice = 5_298m,
                TargetPrice = 5_330m,
                Mode = TradingMode.Practice,
                State = SuggestionState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                Rationale = "entry near the 60m resistance zone",
                Confidence = 55,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CitedFactors =
                [
                    new CitedFactor
                    {
                        Id = factorId,
                        UserId = operatorId,
                        Kind = CitedFactorKind.Level,
                        IsPrimary = true,
                        TimeframeMinutes = 60,
                        // The snapshot -- a COPY of the zone as it stood at issuance, not a reference to it.
                        LevelId = levelId,
                        LevelVenue = "TOPSTEPX",
                        LevelKind = (int)PriceLevelKind.Resistance,
                        LevelTop = 5_310.00m,
                        LevelBottom = 5_305.00m,
                        LevelSignificance = 8.0m,
                    },
                ],
            };
            db.Suggestions.Add(suggestion);
            await db.SaveChangesAsync();
        });

        // (1) MERGE -- an aligned pivot folds into the zone: TouchCount grows, the band re-widens.
        await ExecuteDbAsync(async db =>
        {
            PriceLevel live = await db.PriceLevels.SingleAsync(level => level.Id == levelId);
            live.TouchCount = 4;
            live.Top = 5_312.00m;
            live.Bottom = 5_303.00m;
            live.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        // (2) FLIP -- the detector re-classifies the zone's role (a broken resistance becoming support).
        await ExecuteDbAsync(async db =>
        {
            PriceLevel live = await db.PriceLevels.SingleAsync(level => level.Id == levelId);
            live.Kind = PriceLevelKind.Support;
            live.Significance = 3.0m;
            await db.SaveChangesAsync();
        });

        // (3) RETIRE -- the zone is evicted (no longer live) but the row still exists.
        await ExecuteDbAsync(async db =>
        {
            PriceLevel live = await db.PriceLevels.SingleAsync(level => level.Id == levelId);
            live.Active = false;
            await db.SaveChangesAsync();
        });

        await AssertSnapshotUnchangedAsync(factorId, suggestionId);

        // (4) DELETE -- the row itself is gone. There is no FK to break: this must not throw, and the citation
        // (identified only by the soft LevelId) must still read exactly as it did at issuance.
        await ExecuteDbAsync(async db =>
        {
            PriceLevel live = await db.PriceLevels.SingleAsync(level => level.Id == levelId);
            db.PriceLevels.Remove(live);

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("LevelId is a soft reference -- deliberately no FK (R-4)");
        });

        await AssertSnapshotUnchangedAsync(factorId, suggestionId);

        await ExecuteDbAsync(async db =>
        {
            (await db.PriceLevels.AnyAsync(level => level.Id == levelId)).Should().BeFalse(
                "the source row is genuinely gone, not merely retired -- proving the snapshot survives a real deletion");
        });
    }

    private async Task AssertSnapshotUnchangedAsync(Guid factorId, Guid suggestionId)
    {
        await ExecuteDbAsync(async db =>
        {
            // IgnoreQueryFilters: this scope resolves outside an authenticated HTTP request, so the R-20
            // default-deny filter has no current-user claim to match against -- the sibling suites hit the same
            // shape and use the same escape hatch (SuggestionDatabaseGuardIntegrationTests.ExecuteDbAsync callers).
            CitedFactor snapshot = await db.CitedFactors.IgnoreQueryFilters().SingleAsync(factor => factor.Id == factorId);
            snapshot.LevelKind.Should().Be((int)PriceLevelKind.Resistance, "the snapshot predates the flip to Support");
            snapshot.LevelTop.Should().Be(5_310.00m, "the snapshot predates the merge's re-widened band");
            snapshot.LevelBottom.Should().Be(5_305.00m);
            snapshot.LevelSignificance.Should().Be(8.0m, "the snapshot predates the re-score down to 3.0");
            snapshot.LevelVenue.Should().Be("TOPSTEPX");

            // And the suggestion itself still reads through the same Include a normal read model uses.
            Suggestion loaded = await db.Suggestions
                .IgnoreQueryFilters()
                .Include(suggestion => suggestion.CitedFactors)
                .SingleAsync(suggestion => suggestion.Id == suggestionId);
            loaded.CitedFactors.Should().ContainSingle(factor => factor.Id == factorId);
        });
    }

    /// <summary>
    /// Stands up one <b>Practice</b> account through the real onboarding path, so the mode the R-14 trigger
    /// compares against is the one the app itself resolved -- this suite only needs a valid AccountId, not the
    /// mode guard itself, but reuses the same proven path as the sibling suites for that reason.
    /// </summary>
    private async Task<Guid> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.CitedFactors.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
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

    private Task<Guid> OperatorIdAsync() => QueryDbAsync(async db =>
        (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
