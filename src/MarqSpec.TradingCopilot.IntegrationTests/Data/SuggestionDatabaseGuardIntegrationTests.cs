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
/// The <b>database's own</b> guards on the suggestion journal (gh#552, of epic gh#17; R-4, R-14, R-20) — the last
/// line, below the API, proven against real PostgreSQL with the shipped migrations applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> Nine guards ship on <c>Suggestions</c> / <c>SuggestionDispositions</c> and,
/// before this, <b>not one of them was asserted anywhere</b> — they existed only in the DbContext and a migration.
/// A constraint nobody tests is a constraint nobody knows is still there: it survives a careless
/// <c>HasCheckConstraint</c> edit, a regenerated migration, or a "cleanup" that drops it, and the loss is
/// invisible until bad data is already stored.
/// </para>
/// <para>
/// <b>Invisible to any in-memory provider.</b> These are `CHECK`s, a unique index, a self-referencing
/// <c>RESTRICT</c> foreign key, a column default, and a PL/pgSQL constraint trigger — the EF InMemory provider
/// enforces none of them, so only a container-backed run witnesses them (QA contract §1). Every case therefore
/// writes <b>straight through the DbContext</b>, deliberately bypassing the endpoints: the endpoint guards are
/// separately covered, and what is under test here is what the database refuses when something gets past them.
/// </para>
/// <para>
/// <b>The mode trigger is the sharp one.</b> <c>ct_suggestions_mode_matches_account</c> is R-14's cross-table
/// rule — a suggestion may not claim a mode its account does not hold — and a CHECK cannot express it. It fires on
/// <b>INSERT and on UPDATE of either <c>Mode</c> or <c>AccountId</c></b>, so both update arms are exercised: moving
/// the mode and moving the row to another account are two different ways to end up with a Live-moded suggestion on
/// a Practice account.
/// </para>
/// <para>
/// <b>The supersede chain (gh#550, gh#620)</b> adds two more: <c>SupersedesId</c>'s <c>RESTRICT</c> FK — a
/// superseded row can never be deleted while a later version still points at it, guarded on both the delete-refused
/// and insert-a-dangling-reference sides, each with its own anti-vacuity control — and <c>Version</c>'s database
/// default of <c>1</c>, proven by bypassing the CLR initializer that would otherwise make the test pass identically
/// with no DB default at all (see <see cref="Persistence_ShouldDefaultVersionToOne_WhenNotSupplied"/>).
/// </para>
/// </remarks>
public class SuggestionDatabaseGuardIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private const string Instrument = "ESM25";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SuggestionDatabaseGuardIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // The CHECK constraints — each refuses its own violation, and only its own.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectAnUndeclaredMode_ViaTheAppliedCheckConstraint()
    {
        // R-14: an undeclared account cannot be traded, so nothing should ever be suggested on one. Undeclared is
        // the enum's zero, which is exactly the value a forgotten assignment produces.
        Guid accountId = await FreshAccountAsync();

        await SaveShouldViolateAsync(
            "CK_Suggestions_Mode_NotUndeclared",
            suggestion => suggestion.Mode = TradingMode.Undeclared,
            accountId);
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownState_ViaTheAppliedCheckConstraint()
    {
        // Unknown is the refusable zero: a lifecycle state nothing advances and nothing expires, so a row carrying
        // it would rest forever, invisible to every sweep.
        Guid accountId = await FreshAccountAsync();

        await SaveShouldViolateAsync(
            "CK_Suggestions_State_NotUnknown",
            suggestion => suggestion.State = SuggestionState.Unknown,
            accountId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Persistence_ShouldRejectANonPositiveSize_ViaTheAppliedCheckConstraint(int size)
    {
        // Size comes from the operator's trigger, never the model — but the database is what stops a zero or
        // negative reaching the take path, where it would size a real order.
        Guid accountId = await FreshAccountAsync();

        await SaveShouldViolateAsync(
            "CK_Suggestions_Size_Positive",
            suggestion => suggestion.Size = size,
            accountId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Persistence_ShouldRejectAConfidenceOutsideZeroToOneHundred_ViaTheAppliedCheckConstraint(int confidence)
    {
        // Both ends, because a one-sided range check is the classic half-guard.
        Guid accountId = await FreshAccountAsync();

        await SaveShouldViolateAsync(
            "CK_Suggestions_Confidence_Range",
            suggestion => suggestion.Confidence = confidence,
            accountId);
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnExpiryAtOrBeforeCreation_ViaTheAppliedCheckConstraint()
    {
        // Strictly after: a suggestion that expires the instant it is issued is not a validity window, and one
        // that expires before it was created is incoherent.
        Guid accountId = await FreshAccountAsync();

        await SaveShouldViolateAsync(
            "CK_Suggestions_ExpiresAfterCreated",
            suggestion => suggestion.ExpiresAt = suggestion.CreatedAt,
            accountId);
    }

    [Fact]
    public async Task Persistence_ShouldAcceptAFullyValidSuggestion_SoTheGuardsAboveAreNotRefusingEverything()
    {
        // The anti-vacuity control. Without it, every case above would also pass if `Suggestions` rejected all
        // writes for some unrelated reason — a suite that proves the table is broken, not that the guards work.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();

        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(ValidSuggestion(accountId, operatorId));
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("a valid suggestion must still persist");
        });
    }

    // =============================================================================================================
    // The R-14 mode trigger — a cross-table rule a CHECK cannot express.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectASuggestionWhoseModeDisagreesWithItsAccount_OnInsert()
    {
        // The account is Practice (its firm declares no capital at risk), so a Live-moded suggestion on it is
        // exactly the R-14 confusion the trigger exists to stop.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();

        await ExecuteDbAsync(async db =>
        {
            Suggestion mismatched = ValidSuggestion(accountId, operatorId);
            mismatched.Mode = TradingMode.Live;
            db.Suggestions.Add(mismatched);

            await ShouldRaiseTheModeGuardAsync(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAModeThatIsUpdatedAwayFromItsAccounts_OnUpdate()
    {
        // The trigger fires on UPDATE OF "Mode" too: a row that was valid at insert must not become mismatched by
        // a later edit. An insert-only guard would let exactly that through.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            Suggestion stored = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            stored.Mode = TradingMode.Live;

            await ShouldRaiseTheModeGuardAsync(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAMoveToAnAccountWithADifferentMode_OnUpdateOfAccountId()
    {
        // The second update arm, and the one an "update of Mode" guard alone would miss: the row's mode never
        // changes — the ACCOUNT under it does. Both arms end at the same illegal state.
        Guid practiceAccountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(practiceAccountId, operatorId);
        Guid liveAccountId = await SeedAccountWithModeAsync(operatorId, TradingMode.Live);

        await ExecuteDbAsync(async db =>
        {
            Suggestion stored = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            stored.AccountId = liveAccountId; // Practice-moded row, now hanging off a Live account

            await ShouldRaiseTheModeGuardAsync(() => db.SaveChangesAsync());
        });
    }

    // =============================================================================================================
    // The disposition guards — append-only journal evidence.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectASecondDispositionForOneSuggestion_ViaTheUniqueIndex()
    {
        // One disposition per suggestion: the journal records what the operator DECIDED, not their latest edit.
        // The endpoint pre-checks for a clean 409; this is the backstop against two requests racing past it, and
        // the only thing that makes "append-only" true rather than merely intended.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            db.SuggestionDispositions.Add(NewDisposition(suggestionId, operatorId));
            await db.SaveChangesAsync();
        });

        await ExecuteDbAsync(async db =>
        {
            db.SuggestionDispositions.Add(NewDisposition(suggestionId, operatorId));

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_SuggestionDispositions_SuggestionId"
                        || error.MessageText.Contains("IX_SuggestionDispositions_SuggestionId", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownDispositionKind_ViaTheAppliedCheckConstraint()
    {
        // Kind records an operator ACT — Taken / Modified / Passed. The zero means "nobody decided anything",
        // which is not a disposition and would poison the R-9 learning loop that reads these.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            SuggestionDisposition bad = NewDisposition(suggestionId, operatorId);
            bad.Kind = SuggestionDispositionKind.Unknown;
            db.SuggestionDispositions.Add(bad);

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_SuggestionDispositions_Kind_NotUnknown"
                        || error.MessageText.Contains("CK_SuggestionDispositions_Kind_NotUnknown", StringComparison.Ordinal)));
        });
    }

    // =============================================================================================================
    // The supersede chain (gh#550, R-4 / ADR-0013) — the RESTRICT FK and the Version default, both added by #610
    // and, until now, unasserted anywhere (gh#611 review; gh#620).
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRefuseToDeleteASupersededSuggestion_WhileALaterVersionPointsAtIt()
    {
        // RESTRICT, not cascade: a superseded row can never be silently deleted while a later version still
        // points at it, or the lineage the R-9 loop reads vanishes. Named per the suite's discipline — a bare
        // "it threw" would pass on an unrelated failure.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid supersededId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            Suggestion superseding = ValidSuggestion(accountId, operatorId);
            superseding.SupersedesId = supersededId;
            superseding.Version = 2;
            db.Suggestions.Add(superseding);
            await db.SaveChangesAsync();
        });

        await ExecuteDbAsync(async db =>
        {
            Suggestion superseded = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == supersededId);
            db.Suggestions.Remove(superseded);

            await ShouldViolateTheForeignKeyAsync(
                () => db.SaveChangesAsync(),
                "FK_Suggestions_Suggestions_SupersedesId");
        });
    }

    [Fact]
    public async Task Persistence_ShouldAllowDeletingASuggestionThatNothingSupersedes_SoTheFkGuardIsNotRefusingEveryDelete()
    {
        // The anti-vacuity split for the case above, same reasoning as the CHECK-constraint control: without this,
        // that case would also pass if `Suggestions` refused every delete for an unrelated reason, which proves
        // the table broken rather than the RESTRICT guard working.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid unsupersededId = await SeedValidSuggestionAsync(accountId, operatorId);

        await ExecuteDbAsync(async db =>
        {
            Suggestion unreferenced = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == unsupersededId);
            db.Suggestions.Remove(unreferenced);

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("nothing supersedes this row, so the RESTRICT FK has nothing to guard");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectASupersedesIdPointingAtANonexistentSuggestion_ViaTheAppliedForeignKey()
    {
        // The other half of what an FK is for: it also refuses a dangling reference at insert time, not only a
        // delete that would create one.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();

        await ExecuteDbAsync(async db =>
        {
            Suggestion dangling = ValidSuggestion(accountId, operatorId);
            dangling.SupersedesId = Guid.NewGuid();
            dangling.Version = 2;
            db.Suggestions.Add(dangling);

            await ShouldViolateTheForeignKeyAsync(
                () => db.SaveChangesAsync(),
                "FK_Suggestions_Suggestions_SupersedesId");
        });
    }

    [Fact]
    public async Task Persistence_ShouldDefaultVersionToOne_WhenNotSupplied()
    {
        // Version defaults to 1 at the DATABASE (gh#550) — the DB default is what makes the superseding row's
        // Version meaningful (+1); if it silently became 0, the chain's ordering is wrong and nothing else would
        // notice. The CLR initializer on Suggestion.Version already sets 1, so a plain ValidSuggestion() insert
        // would pass identically even with no DB default at all — it would prove the C# initializer, not the
        // guard #610 shipped. Setting Version to the CLR sentinel (0) before adding is what makes EF omit the
        // column from the INSERT and defer to the database's DEFAULT, the same path a raw INSERT that never
        // mentions Version takes.
        Guid accountId = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            Suggestion unversioned = ValidSuggestion(accountId, operatorId);
            unversioned.Id = suggestionId;
            unversioned.Version = default;
            db.Suggestions.Add(unversioned);
            await db.SaveChangesAsync();
        });

        await ExecuteDbAsync(async db =>
        {
            Suggestion stored = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            stored.Version.Should().Be(1, "the DB default is what the superseding row's Version is one higher than");
        });
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    /// <summary>
    /// Applies <paramref name="violate"/> to an otherwise-valid suggestion and asserts the named CHECK refuses it.
    /// Naming the constraint is the point: a bare "it threw" would pass when a *different* guard fired, which is
    /// how a suite ends up proving the wrong invariant.
    /// </summary>
    private async Task SaveShouldViolateAsync(string constraint, Action<Suggestion> violate, Guid accountId)
    {
        Guid operatorId = await OperatorIdAsync();

        await ExecuteDbAsync(async db =>
        {
            Suggestion bad = ValidSuggestion(accountId, operatorId);
            violate(bad);
            db.Suggestions.Add(bad);

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == constraint
                        || error.MessageText.Contains(constraint, StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// Asserts the R-14 mode trigger refused the write. It is a PL/pgSQL <c>RAISE EXCEPTION</c>, so it surfaces as
    /// <see cref="PostgresErrorCodes.RaiseException"/> carrying the guard's message — not as a constraint name.
    /// </summary>
    private static async Task ShouldRaiseTheModeGuardAsync(Func<Task> save)
    {
        await save.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.RaiseException
                && error.MessageText.Contains("R-14 mode guard", StringComparison.Ordinal));
    }

    /// <summary>
    /// Asserts the named FOREIGN KEY refused the write — the same naming discipline as
    /// <see cref="SaveShouldViolateAsync"/>, for the FK guards rather than a CHECK.
    /// </summary>
    private static async Task ShouldViolateTheForeignKeyAsync(Func<Task> save, string constraint)
    {
        await save.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && (error.ConstraintName == constraint
                    || error.MessageText.Contains(constraint, StringComparison.Ordinal)));
    }

    private static Suggestion ValidSuggestion(Guid accountId, Guid operatorId) => new()
    {
        Id = Guid.NewGuid(),
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
        CitedIndicator = "rsi",
        CitedPeriod = 14,
        CitedResolutionMinutes = 1,
        Confidence = 50,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    private static SuggestionDisposition NewDisposition(Guid suggestionId, Guid operatorId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = operatorId,
        SuggestionId = suggestionId,
        Kind = SuggestionDispositionKind.Passed,
        Reasons = SuggestionPassReason.None,
        CreatedAt = DateTimeOffset.UtcNow,
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
    /// the mode the trigger compares against is the one the app itself resolved from the firm's conventions.
    /// </summary>
    private async Task<Guid> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
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

    /// <summary>
    /// A second account carrying <paramref name="mode"/>, written directly — the onboarding path resolves mode from
    /// conventions and will not mint a Live one here, and what this suite needs is simply an account the trigger
    /// will disagree with.
    /// </summary>
    private async Task<Guid> SeedAccountWithModeAsync(Guid operatorId, TradingMode mode)
    {
        Guid accountId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            Connection connection = await db.Connections.IgnoreQueryFilters().FirstAsync();
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = operatorId,
                ConnectionId = connection.Id,
                VenueAccountKey = $"MODE-{Guid.NewGuid():N}"[..16],
                Name = "mode-fixture",
                Stage = AccountStage.Evaluation,
                Mode = mode,
            });
            await db.SaveChangesAsync();
        });
        return accountId;
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
