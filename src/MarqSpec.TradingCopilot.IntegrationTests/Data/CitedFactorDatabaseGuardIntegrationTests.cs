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
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>database's own</b> guards on the multi-cited-factor set (gh#958, QA for gh#729; R-4, ADR-0026) —
/// written independently of the dev PR, mirroring <see cref="SuggestionDatabaseGuardIntegrationTests"/>'s
/// pattern for the parent journal. Proven against real PostgreSQL with the shipped migrations applied, because
/// every guard here is a <c>CHECK</c>, a <b>partial</b> unique index, or an <c>ON DELETE CASCADE</c> foreign key —
/// none of which the EF InMemory provider enforces (QA contract §1). Every case writes <b>straight through the
/// DbContext</b>, bypassing the issuance endpoint on purpose: what is under test is what the database itself
/// refuses once something gets past the application layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cascade, not a CHECK.</b> A <see cref="CitedFactor"/> is a child of its <see cref="Suggestion"/>
/// (<c>onDelete: Cascade</c>): the journal never keeps an orphaned factor once its suggestion is gone. Proving
/// cascade needs more than "the delete did not throw" — a missing or misconfigured cascade would still let the
/// delete succeed while leaving the children behind, so the assertion re-queries the child table directly.
/// </para>
/// <para>
/// <b>Why the unique index needs an anti-vacuity control.</b>
/// <c>UX_SuggestionCitedFactors_OnePrimary</c> is <b>partial</b> — filtered to <c>WHERE "IsPrimary"</c> — so a
/// second <i>supporting</i> row for the same suggestion must still succeed; without that control, the "second
/// primary is refused" case would also pass if the table refused every second row for an unrelated reason.
/// </para>
/// <para>
/// <b>The kind/columns pairing overlaps the not-unknown check by construction.</b>
/// <c>CK_SuggestionCitedFactors_KindColumns</c> requires <c>Kind</c> to be exactly 1 or 2, so a <c>Kind = 0</c>
/// row always violates it too, and it is <c>KindColumns</c> Postgres actually reports for that row (verified
/// against the real container) — not <c>Kind_NotUnknown</c>, contrary to this suite's first assumption that
/// declaration order would decide it. The not-unknown case is written with every arm column left null so it
/// cannot ALSO trip <c>CK_SuggestionCitedFactors_LevelZoneOrdered</c> or the level-kind check, and asserts the
/// constraint actually raised.
/// </para>
/// </remarks>
public class CitedFactorDatabaseGuardIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private const string Instrument = "ESM25";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public CitedFactorDatabaseGuardIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // The cascade — a factor never outlives its suggestion.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldCascadeDeleteCitedFactors_WhenTheirSuggestionIsDeleted()
    {
        // Not just "the delete didn't throw" — a missing cascade would let the delete succeed and leave the
        // children behind, so this re-queries the child table directly rather than trusting SaveChanges alone.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            Suggestion suggestion = ValidSuggestion(accountId, operatorId, suggestionId);
            suggestion.CitedFactors.Add(SupportingLevelFactor(operatorId, Guid.NewGuid()));
            db.Suggestions.Add(suggestion);
            await db.SaveChangesAsync();
        });

        await ExecuteDbAsync(async db =>
        {
            int before = await db.CitedFactors.IgnoreQueryFilters()
                .CountAsync(factor => factor.SuggestionId == suggestionId);
            before.Should().Be(2, "the seed carries a primary indicator factor plus one supporting level factor");

            Suggestion stored = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            db.Suggestions.Remove(stored);

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("the FK is ON DELETE CASCADE, not RESTRICT");
        });

        await ExecuteDbAsync(async db =>
        {
            int after = await db.CitedFactors.IgnoreQueryFilters()
                .CountAsync(factor => factor.SuggestionId == suggestionId);
            after.Should().Be(0, "a factor must never outlive the suggestion it cites");
        });
    }

    // =============================================================================================================
    // The partial unique index — exactly one primary per suggestion, supporting rows unconstrained.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectASecondPrimaryFactor_ViaThePartialUniqueIndex()
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            db.CitedFactors.Add(new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                SuggestionId = suggestionId,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = true, // A second primary for a suggestion that already has one.
                TimeframeMinutes = 60,
                Indicator = "ema",
                Period = 200,
            });

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "UX_SuggestionCitedFactors_OnePrimary"
                        || error.MessageText.Contains("UX_SuggestionCitedFactors_OnePrimary", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Persistence_ShouldAllowASecondSupportingFactor_SoThePartialIndexIsNotRefusingEveryDuplicate()
    {
        // The anti-vacuity control: without it, the case above would also pass if the table refused every second
        // row for the same suggestion regardless of IsPrimary, which would prove nothing about the PARTIAL index.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            db.CitedFactors.Add(new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                SuggestionId = suggestionId,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = false,
                TimeframeMinutes = 60,
                Indicator = "ema",
                Period = 200,
            });

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("the unique index is filtered to IsPrimary rows only");
        });

        await ExecuteDbAsync(async db =>
        {
            int count = await db.CitedFactors.IgnoreQueryFilters()
                .CountAsync(factor => factor.SuggestionId == suggestionId);
            count.Should().Be(2, "the original primary plus the new supporting row");
        });
    }

    // =============================================================================================================
    // The kind/columns pairing — the arm can never disagree with the kind.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectAnIndicatorFactor_ThatAlsoCarriesLevelColumns_ViaTheKindColumnsCheck()
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            CitedFactor halfBuilt = SupportingLevelFactor(operatorId, Guid.NewGuid());
            halfBuilt.SuggestionId = suggestionId;
            halfBuilt.Kind = CitedFactorKind.Indicator;
            halfBuilt.Indicator = "rsi"; // The indicator arm filled in...
            halfBuilt.Period = 14;
            // ...but the level arm (LevelId/LevelVenue/LevelKind/LevelTop/LevelBottom/LevelSignificance) is left
            // populated by SupportingLevelFactor(), so the row satisfies neither disjunct of the pairing check.
            db.CitedFactors.Add(halfBuilt);

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_KindColumns");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectALevelFactor_ThatAlsoCarriesIndicatorColumns_ViaTheKindColumnsCheck()
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            CitedFactor halfBuilt = SupportingLevelFactor(operatorId, Guid.NewGuid());
            halfBuilt.SuggestionId = suggestionId;
            // The level arm is already filled in by SupportingLevelFactor(); layering the indicator columns on
            // top means neither disjunct of the pairing check holds.
            halfBuilt.Indicator = "rsi";
            halfBuilt.Period = 14;
            db.CitedFactors.Add(halfBuilt);

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_KindColumns");
        });
    }

    [Fact]
    public async Task Persistence_ShouldAcceptAWellFormedLevelFactor_SoTheKindColumnsCheckIsNotRefusingEveryLevelRow()
    {
        // Anti-vacuity control for both KindColumns cases above, and for the level-arm scalar checks below: a
        // properly-shaped Level factor (the exact opposite arm from the suite's Indicator-seeded baseline) must
        // still persist.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            CitedFactor wellFormed = SupportingLevelFactor(operatorId, Guid.NewGuid());
            wellFormed.SuggestionId = suggestionId;
            db.CitedFactors.Add(wellFormed);

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("a well-formed level snapshot must persist");
        });
    }

    // =============================================================================================================
    // The fail-closed-zero and scalar checks.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownKind_ViaTheAppliedCheckConstraint()
    {
        // Kind = 0 (Unknown) is written with every arm column left null -- as close to isolating this one
        // violation as the schema allows, since Kind = 0 unavoidably ALSO fails CK_SuggestionCitedFactors_KindColumns
        // (neither of its disjuncts accepts a Kind other than 1 or 2). Confirmed against the real container that
        // Postgres reports KindColumns for this row, not Kind_NotUnknown -- both checks genuinely refuse it, and
        // this asserts the one actually raised rather than the one this suite originally assumed would be. The
        // Kind_NotUnknown check still earns its own row: it is what refuses Kind = 0 when the columns are already
        // well-formed for neither arm, which is every possible zero-Kind row given the arms are mutually exclusive.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            db.CitedFactors.Add(new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                SuggestionId = suggestionId,
                Kind = CitedFactorKind.Unknown,
                IsPrimary = false,
                TimeframeMinutes = 15,
            });

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_KindColumns");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Persistence_ShouldRejectANonPositiveTimeframe_ViaTheAppliedCheckConstraint(int timeframeMinutes)
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            db.CitedFactors.Add(new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                SuggestionId = suggestionId,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = false,
                TimeframeMinutes = timeframeMinutes,
                Indicator = "rsi",
                Period = 14,
            });

            await ShouldViolateTheCheckAsync(
                () => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_Timeframe_Positive");
        });
    }

    [Theory]
    [InlineData(100, 100)] // Equal -- not strictly above.
    [InlineData(100, 105)] // Reversed.
    public async Task Persistence_ShouldRejectALevelZoneThatIsNotOrdered_ViaTheAppliedCheckConstraint(
        int top, int bottom)
    {
        // int InlineData, not decimal -- attribute arguments must be compile-time constants and decimal is not
        // an allowed type there; the values are converted for the actual price columns below.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            CitedFactor bad = SupportingLevelFactor(operatorId, Guid.NewGuid());
            bad.SuggestionId = suggestionId;
            bad.LevelTop = top;
            bad.LevelBottom = bottom;
            db.CitedFactors.Add(bad);

            await ShouldViolateTheCheckAsync(
                () => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_LevelZoneOrdered");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownLevelKind_ViaTheAppliedCheckConstraint()
    {
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            CitedFactor bad = SupportingLevelFactor(operatorId, Guid.NewGuid());
            bad.SuggestionId = suggestionId;
            bad.LevelKind = (int)PriceLevelKind.Unknown;
            db.CitedFactors.Add(bad);

            await ShouldViolateTheCheckAsync(
                () => db.SaveChangesAsync(), "CK_SuggestionCitedFactors_LevelKind_NotUnknown");
        });
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private static async Task ShouldViolateTheCheckAsync(Func<Task> save, string constraint)
    {
        await save.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                && (error.ConstraintName == constraint
                    || error.MessageText.Contains(constraint, StringComparison.Ordinal)));
    }

    private static Suggestion ValidSuggestion(Guid accountId, Guid operatorId, Guid? id = null) => new()
    {
        Origin = SuggestionOrigin.Scan,
        Id = id ?? Guid.NewGuid(),
        UserId = operatorId,
        AccountId = accountId,
        Instrument = Instrument,
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5_000m,
        StopPrice = 4_990m,
        TargetPrice = 5_020m,
        Mode = TradingMode.Practice,
        State = SuggestionState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        Rationale = "seeded",
        CitedFactors =
        [
            new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = true,
                TimeframeMinutes = 5,
                Indicator = "rsi",
                Period = 14,
            },
        ],
        Confidence = 50,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    /// <summary>A well-formed, supporting (never primary) Level-arm factor -- every level column filled in.</summary>
    private static CitedFactor SupportingLevelFactor(Guid operatorId, Guid levelId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = operatorId,
        Kind = CitedFactorKind.Level,
        IsPrimary = false,
        TimeframeMinutes = 60,
        LevelId = levelId,
        LevelVenue = "TOPSTEPX",
        LevelKind = (int)PriceLevelKind.Resistance,
        LevelTop = 5_310.00m,
        LevelBottom = 5_305.00m,
        LevelSignificance = 8.0m,
    };

    private async Task<Guid> SeedValidSuggestionAsync(Guid accountId, Guid operatorId)
    {
        Suggestion suggestion = ValidSuggestion(accountId, operatorId);
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(suggestion);
            await db.SaveChangesAsync();
        });
        return suggestion.Id;
    }

    /// <summary>
    /// Wipes the suggestion journal and stands up one <b>Practice</b> account through the real onboarding path, so
    /// the mode the R-14 trigger compares against is the one the app itself resolved from the firm's conventions.
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
