using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for <see cref="OutcomeSuppression"/> (gh#965, of gh#955; R-9 / R-15 / R-20) —
/// the recomposition-suppression tombstone that makes an <see cref="Outcome"/> hard delete <b>stick</b>. Written from
/// gh#955's own text and the R-15 requirement, not from <see cref="OutcomeEndpoints.HardDeleteAsync"/> or
/// <see cref="OutcomeJournalService"/>. What only real Postgres witnesses: <c>CK_OutcomeSuppressions_OneParent</c>
/// and the two unique filtered indexes are DB-enforced, not model-level, and the "sticks against the live sweep"
/// claim needs a REAL <see cref="OutcomeJournalService"/> pass reading committed rows back through a fresh scope —
/// an EF-InMemory unit double proves neither.
/// </summary>
/// <remarks>
/// Uses <see cref="OutcomeTestPostgresFactory"/> (every hosted service stripped, including the writer's own poll),
/// so the ONLY sweep pass in these tests is the one each case drives by hand — a stray pass is never the reason a
/// "still suppressed" assertion holds. Outcomes have no create endpoint, so every fixture row is seeded straight
/// through the <see cref="TradingCopilotDbContext"/>, and the confirmed hard delete itself goes through the real
/// <c>DELETE /outcomes/{id}</c> HTTP surface (the only production writer of a tombstone), matching
/// <see cref="OutcomeRemovalSurfaceIntegrationTests"/>' own pattern.
/// </remarks>
public class OutcomeSuppressionIntegrationTests : IClassFixture<OutcomeTestPostgresFactory>
{
    private readonly OutcomeTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public OutcomeSuppressionIntegrationTests(OutcomeTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // CK_OutcomeSuppressions_OneParent — exactly one key, enforced.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectATwoNullSuppression_ViaTheOneParentCheck()
    {
        Guid owner = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = null,
                SuggestionId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_OutcomeSuppressions_OneParent");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectABothSetSuppression_ViaTheOneParentCheck()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 10m);
        // The rejected insert below never lands (the CK fires first), so this trade AND suggestion would otherwise
        // sit in the shared class-fixture container as genuinely composable rows forever — resolve both directly so
        // a LATER case's real sweep run can never pick either up and pollute an unrelated assertion's "written" count.
        await SeedOutcomeAsync(new Outcome
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            TradeId = tradeId,
            Resolution = OutcomeResolution.Win,
            Simulated = false,
        });
        await SeedOutcomeAsync(new Outcome
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            SuggestionId = suggestionId,
            Resolution = OutcomeResolution.Expired,
            Simulated = false,
        });

        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeId,
                SuggestionId = suggestionId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_OutcomeSuppressions_OneParent");
        });
    }

    [Fact]
    public async Task Persistence_ShouldAcceptAOneKeySuppression_SoTheCheckIsNotRefusingEverything()
    {
        // The anti-vacuity control: without it, both refusals above would also pass if OutcomeSuppressions rejected
        // every write for some unrelated reason, proving the table broken rather than the guard working.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 10m);

        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeId,
                SuggestionId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("a one-key tombstone is exactly the shape the writer builds");
        });
    }

    // =============================================================================================================
    // The unique filtered indexes — idempotent suppression.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectASecondTombstoneForTheSameTrade_ViaTheUniqueIndex()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 10m);
        await SeedTombstoneAsync(owner, tradeId: tradeId, suggestionId: null);

        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeId,
                SuggestionId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>("a second tombstone for the same trade must be impossible")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_OutcomeSuppressions_TradeId"
                        || error.MessageText.Contains("IX_OutcomeSuppressions_TradeId", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectASecondTombstoneForTheSameSuggestion_ViaTheUniqueIndex()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId);
        await SeedTombstoneAsync(owner, tradeId: null, suggestionId: suggestionId);

        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = null,
                SuggestionId = suggestionId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>("a second tombstone for the same suggestion must be impossible")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_OutcomeSuppressions_SuggestionId"
                        || error.MessageText.Contains("IX_OutcomeSuppressions_SuggestionId", StringComparison.Ordinal)));
        });
    }

    // =============================================================================================================
    // The delete sticks against the LIVE sweep — the anti-join only a real host + DB can exercise.
    // =============================================================================================================

    [Fact]
    public async Task HardDelete_ShouldStick_WhenTheRealClosedTradeSweepRunsAfterward()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid accountId = await SeedAccountAsync(userId);
        Guid tradeId = await SeedClosedTradeAsync(userId, accountId, realizedPnL: 75m);
        Guid outcomeId = await SeedOutcomeAsync(new Outcome
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TradeId = tradeId,
            Resolution = OutcomeResolution.Win,
            Simulated = false,
        });

        using HttpResponseMessage response = await client.DeleteAsync($"/outcomes/{outcomeId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int written = await RunClosedTradeSweepAsync();

        written.Should().Be(
            0, "the trade still reads closed with a realized result — without the tombstone anti-join the real "
            + "sweep would recompose it inside one poll");
        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse("the confirmed removal must stay removed");
    }

    [Fact]
    public async Task HardDelete_ShouldStick_WhenTheRealUnfilledSuggestionSweepRunsAfterward()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid accountId = await SeedAccountAsync(userId);
        Guid suggestionId = await SeedSuggestionAsync(userId, accountId, SuggestionState.ExpiredVoid);
        Guid outcomeId = await SeedOutcomeAsync(new Outcome
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SuggestionId = suggestionId,
            Resolution = OutcomeResolution.Expired,
            Simulated = false,
        });

        using HttpResponseMessage response = await client.DeleteAsync($"/outcomes/{outcomeId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int written = await RunUnfilledSuggestionSweepAsync();

        written.Should().Be(
            0, "the suggestion still reads terminal and unfilled — without the tombstone anti-join the real sweep "
            + "would recompose it inside one poll");
        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse("the confirmed removal must stay removed");
    }

    // =============================================================================================================
    // Cascade integrity via account removal.
    // =============================================================================================================

    [Fact]
    public async Task AccountRemoval_ShouldCascadeAwayATradeKeyedTombstone_WithNoOrphanOrConstraintBreach()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 10m);
        Guid tombstoneId = await SeedTombstoneAsync(owner, tradeId: tradeId, suggestionId: null);

        await ExecuteDbAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            db.Accounts.Remove(account);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync(
                "Account -> Trade -> OutcomeSuppression is Cascade at every hop");
        });

        (await TombstoneExistsAsync(tombstoneId)).Should().BeFalse(
            "the trade-keyed tombstone dies with the account that owned its trade");
    }

    [Fact]
    public async Task AccountRemoval_ShouldCascadeAwayASuggestionKeyedTombstone_WithNoOrphanOrConstraintBreach()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId);
        Guid tombstoneId = await SeedTombstoneAsync(owner, tradeId: null, suggestionId: suggestionId);

        await ExecuteDbAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            db.Accounts.Remove(account);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync(
                "Account -> Suggestion -> OutcomeSuppression is Cascade at every hop");
        });

        (await TombstoneExistsAsync(tombstoneId)).Should().BeFalse(
            "the suggestion-keyed tombstone dies with the account that owned its suggestion");
    }

    // =============================================================================================================
    // R-20 — owner-stamped, and the sweep anti-join is keyed globally (suppression silences only its own source).
    // =============================================================================================================

    [Fact]
    public async Task Tombstone_ShouldBeOwnerStamped_AndOnlySilenceItsOwnSource_NeverAnUnrelatedOwnersTrade()
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient _, Guid userIdB) = await CreateSecondOperatorAsync(operatorA);
        Guid userIdA = await OperatorUserIdAsync();

        Guid accountA = await SeedAccountAsync(userIdA);
        Guid accountB = await SeedAccountAsync(userIdB);
        Guid tradeA = await SeedClosedTradeAsync(userIdA, accountA, realizedPnL: 20m);
        Guid tradeB = await SeedClosedTradeAsync(userIdB, accountB, realizedPnL: -20m); // deliberately left un-outcomed
        Guid outcomeA = await SeedOutcomeAsync(new Outcome
        {
            Id = Guid.NewGuid(),
            UserId = userIdA,
            TradeId = tradeA,
            Resolution = OutcomeResolution.Win,
            Simulated = false,
        });

        using HttpResponseMessage response = await operatorA.DeleteAsync($"/outcomes/{outcomeA}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Scoped to tradeA — the shared class-fixture container can carry another case's unrelated tombstone.
        OutcomeSuppression tombstone = await QueryDbAsync(db =>
            db.OutcomeSuppressions.IgnoreQueryFilters().SingleAsync(s => s.TradeId == tradeA));
        tombstone.UserId.Should().Be(userIdA, "the tombstone is stamped from the deleted outcome's own owner (R-20)");

        int written = await RunClosedTradeSweepAsync();

        written.Should().Be(
            1, "the cross-owner sweep must still compose B's unrelated, never-outcomed trade — a suppression "
            + "silences precisely the source it names, never the whole sweep or the whole owner");
        (await OutcomeExistsAsync(outcomeA)).Should().BeFalse("A's confirmed removal stays removed — the sweep must not resurrect it");
        (await OutcomesForTradeAsync(tradeB)).Should().ContainSingle(
            "a suppression keyed on A's trade must never silence the sweep for B's unrelated trade");
    }

    // =============================================================================================================
    // Audit survives a failed secondary write — the removal + suppression have already committed.
    // =============================================================================================================

    [Fact]
    public async Task HardDelete_ShouldStillRemoveAndSuppress_OnRealPostgres_WhenTheAuditWriteFails()
    {
        // A dedicated host + its OWN throwaway Postgres container (gh#152's manual-lifecycle pattern) — swapping
        // IAuditLog on the class fixture's shared container would perturb every other case in this suite, and an
        // audit fault must never be the explanation for any other test's result.
        OutcomeSuppressionAuditFailureTestPostgresFactory throwingAuditFactory = new();
        await throwingAuditFactory.InitializeAsync();
        try
        {
            HttpClient client = await AuthenticatedOperatorClientAsync(throwingAuditFactory);
            Guid userId = await OperatorUserIdAsync(throwingAuditFactory);
            Guid accountId = await SeedAccountAsync(userId, throwingAuditFactory);
            Guid tradeId = await SeedClosedTradeAsync(userId, accountId, realizedPnL: 15m, throwingAuditFactory);
            Guid outcomeId = await SeedOutcomeAsync(new Outcome
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TradeId = tradeId,
                Resolution = OutcomeResolution.Win,
                Simulated = false,
            }, throwingAuditFactory);

            using HttpResponseMessage response = await client.DeleteAsync($"/outcomes/{outcomeId}");

            response.StatusCode.Should().Be(
                HttpStatusCode.NoContent, "a failed SECONDARY audit write must never surface as the request's outcome");

            await using AsyncServiceScope scope = throwingAuditFactory.Services.CreateAsyncScope();
            TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            (await database.Outcomes.IgnoreQueryFilters().AnyAsync(o => o.Id == outcomeId)).Should().BeFalse(
                "the row removal committed before the audit ran and must not be rolled back by its failure");
            (await database.OutcomeSuppressions.IgnoreQueryFilters().AnyAsync(s => s.TradeId == tradeId)).Should().BeTrue(
                "the tombstone committed in the SAME unit of work as the remove — it must stick regardless of the audit");
        }
        finally
        {
            await ((IAsyncLifetime)throwingAuditFactory).DisposeAsync();
        }
    }

    // =============================================================================================================
    // A dedicated host that swaps IAuditLog for one that always throws, and its own throwaway container (manual
    // lifecycle — never registered as this class's IClassFixture, so xUnit never starts or stops it itself).
    // =============================================================================================================

    private sealed class OutcomeSuppressionAuditFailureTestPostgresFactory : StubbedVenuePostgresFactory
    {
        // Mirrors OutcomeTestPostgresFactory (sealed, so re-derived rather than subclassed): strips every hosted
        // service, THEN swaps IAuditLog for one that always throws.
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);

            foreach (ServiceDescriptor hosted in services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .ToList())
            {
                services.Remove(hosted);
            }

            services.RemoveAll<IAuditLog>();
            services.AddScoped<IAuditLog, ThrowingAuditLog>();
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task WriteAsync(IReadOnlyCollection<AuditRecord> records, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("audit sink down (gh#965 prove-red fixture)");
    }

    // =============================================================================================================
    // Operators.
    // =============================================================================================================

    private async Task<HttpClient> AuthenticatedOperatorClientAsync(PostgresApiFactory? factory = null)
    {
        HttpClient client = (factory ?? _factory).CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"suppression-b-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "SuppressionB-Pass123!", "Suppression Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        Guid userId = await QueryDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return (client, userId);
    }

    private Task<Guid> OperatorUserIdAsync(PostgresApiFactory? factory = null) => QueryDbAsync(
        db => db.Users.Where(u => u.Email == PostgresApiFactory.OperatorEmail).Select(u => u.Id).SingleAsync(),
        factory);

    // =============================================================================================================
    // Sweep drivers.
    // =============================================================================================================

    private async Task<int> RunClosedTradeSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
        return await service.ComposeClosedTradeOutcomesAsync(CancellationToken.None);
    }

    private async Task<int> RunUnfilledSuggestionSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
        return await service.ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None);
    }

    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    private async Task<Guid> SeedTombstoneAsync(Guid owner, Guid? tradeId, Guid? suggestionId)
    {
        Guid id = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.OutcomeSuppressions.Add(new OutcomeSuppression
            {
                Id = id,
                UserId = owner,
                TradeId = tradeId,
                SuggestionId = suggestionId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<Guid> SeedOutcomeAsync(Outcome outcome, PostgresApiFactory? factory = null)
    {
        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(outcome);
            await db.SaveChangesAsync();
        }, factory);
        return outcome.Id;
    }

    private async Task<Guid> SeedAccountAsync(Guid owner, PostgresApiFactory? factory = null)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            // Several cases in this suite share the real bootstrap operator across the class-fixture container
            // (the DELETE surface needs a real authenticated user), so the Firm name must be unique per seed call
            // to avoid tripping the real IX_Firms_UserId_Name unique index (OutcomeRemovalSurfaceIntegrationTests'
            // own fix for the same collision).
            db.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = $"Topstep-{Guid.NewGuid():N}", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = $"key-{Guid.NewGuid():N}"[..16],
            });
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = $"ACC-{Guid.NewGuid():N}"[..16],
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            await db.SaveChangesAsync();
        }, factory);

        return accountId;
    }

    private async Task<Guid> SeedSuggestionAsync(Guid owner, Guid accountId, SuggestionState state = SuggestionState.ExpiredVoid)
    {
        Guid suggestionId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Origin = SuggestionOrigin.Scan,
                Id = suggestionId,
                UserId = owner,
                AccountId = accountId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                StopPrice = 4_990m,
                TargetPrice = 5_020m,
                Mode = TradingMode.Practice,
                State = state,
                CreatedAt = DateTimeOffset.UtcNow,
                Rationale = "gh#965 fixture",
                Confidence = 50,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        });

        return suggestionId;
    }

    private async Task<Guid> SeedClosedTradeAsync(
        Guid owner, Guid accountId, decimal realizedPnL, PostgresApiFactory? factory = null)
    {
        Guid tradeId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(new Trade
            {
                Id = tradeId,
                UserId = owner,
                AccountId = accountId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                ExitPrice = 5_000m + realizedPnL,
                RealizedPnL = realizedPnL,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }, factory);
        return tradeId;
    }

    private async Task<bool> OutcomeExistsAsync(Guid id) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().AnyAsync(o => o.Id == id));

    private async Task<List<Outcome>> OutcomesForTradeAsync(Guid tradeId) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().Where(o => o.TradeId == tradeId).ToListAsync());

    private async Task<bool> TombstoneExistsAsync(Guid id) => await QueryDbAsync(db =>
        db.OutcomeSuppressions.IgnoreQueryFilters().AnyAsync(s => s.Id == id));

    private static async Task ShouldViolateTheCheckAsync(Func<Task> save, string constraint)
    {
        await save.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                && (error.ConstraintName == constraint
                    || error.MessageText.Contains(constraint, StringComparison.Ordinal)));
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query, PostgresApiFactory? factory = null)
    {
        await using AsyncServiceScope scope = (factory ?? _factory).Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action, PostgresApiFactory? factory = null)
    {
        await using AsyncServiceScope scope = (factory ?? _factory).Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }
}
