using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The <c>/outcomes</c> R-20 isolation, report-toggle and hard-delete-audit surface end to end over real Postgres
/// (gh#940, of gh#909; R-9 / R-15 / R-20) — written from gh#940's own text, not from <c>OutcomeEndpoints</c>. The
/// unit tier (<c>OutcomeEndpointsTests</c>) proves the same shapes against EF-InMemory with a faked
/// <c>IAuditLog</c>; what only a real host and a real database witness is that a foreign outcome really is a 404
/// through the live per-user query filter (not a hand-rolled ownership check), and that the hard delete's audit
/// row really does satisfy <c>CK_AuditRecords_Source_MatchesAction</c> — an in-memory <c>IAuditLog</c> applies no
/// check constraint at all.
/// </summary>
/// <remarks>
/// Operator B is created through the real invitation flow (the same pattern <c>MultiTenantIsolationIntegrationTests</c>
/// uses), so R-20 is proven at the HTTP surface between two genuinely distinct, authenticated operators. Every
/// outcome is seeded straight through the <see cref="TradingCopilotDbContext"/>, stamped with the real operator's
/// id looked up from <c>Users</c> — outcomes have no create endpoint, so this is the surface's only way in. Uses
/// <see cref="OutcomeTestPostgresFactory"/> (hosted services stripped) so the report-toggle counts are never
/// perturbed by the writer's own background sweep.
/// </remarks>
public class OutcomeRemovalSurfaceIntegrationTests : IClassFixture<OutcomeTestPostgresFactory>
{
    private readonly OutcomeTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public OutcomeRemovalSurfaceIntegrationTests(OutcomeTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // R-20 isolation — every mutating action, and the list.
    // =============================================================================================================

    [Fact]
    public async Task List_ShouldNeverIncludeAnotherOperatorsOutcome()
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, _) = await CreateSecondOperatorAsync(operatorA);
        Guid userIdA = await OperatorUserIdAsync();
        Guid outcomeA = await SeedOwnedOutcomeAsync(userIdA);

        List<OutcomeResponse> seenByA = await ListAsync(operatorA, includeDeleted: true);
        seenByA.Should().Contain(o => o.Id == outcomeA, "the owning operator sees its own outcome");

        List<OutcomeResponse> seenByB = await ListAsync(operatorB, includeDeleted: true);
        seenByB.Should().NotContain(o => o.Id == outcomeA, "a second operator must never enumerate operator A's outcomes");
    }

    public static TheoryData<string> MutatingRoutes() => new()
    {
        "soft-delete",
        "restore",
        "training-exclusion",
        "visibility",
        "hard-delete",
    };

    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task MutatingAction_ShouldReturn404_ForAForeignOutcome_AndLeaveItUntouched(string route)
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, _) = await CreateSecondOperatorAsync(operatorA);
        Guid userIdA = await OperatorUserIdAsync();
        Guid outcomeA = await SeedOwnedOutcomeAsync(userIdA);

        HttpResponseMessage response = route switch
        {
            "soft-delete" => await operatorB.PostAsync($"/outcomes/{outcomeA}/soft-delete", content: null),
            "restore" => await operatorB.PostAsync($"/outcomes/{outcomeA}/restore", content: null),
            "training-exclusion" => await operatorB.PutAsJsonAsync($"/outcomes/{outcomeA}/training-exclusion", new { value = true }),
            "visibility" => await operatorB.PutAsJsonAsync($"/outcomes/{outcomeA}/visibility", new { value = true }),
            "hard-delete" => await operatorB.DeleteAsync($"/outcomes/{outcomeA}"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unmapped route"),
        };
        using (response)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.NotFound, $"operator B must not be able to '{route}' operator A's outcome (R-20)");
        }

        (await OutcomeStillOwnedByAsync(outcomeA, userIdA)).Should().BeTrue(
            "the foreign attempt must leave the outcome exactly as it was — untouched and still A's");
        (await AuditRowMentionsAsync(outcomeA)).Should().BeFalse(
            "a 404 mutates nothing, so it audits nothing either — scoped to THIS outcome, since the shared "
            + "container-per-class fixture may carry an unrelated audit row from another case in this class");
    }

    // =============================================================================================================
    // The report toggle.
    // =============================================================================================================

    [Fact]
    public async Task List_IncludeDeletedToggle_ShouldReturnTheInclusiveVsExclusiveFigures_ForTheSameSet()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid kept = await SeedOwnedOutcomeAsync(userId);
        Guid removed = await SeedOwnedOutcomeAsync(userId, softDeleted: true);

        List<OutcomeResponse> exclusive = await ListAsync(client, includeDeleted: null);
        exclusive.Select(o => o.Id).Should().Contain(kept)
            .And.NotContain(removed, "the default excludes soft-deleted rows");

        List<OutcomeResponse> inclusive = await ListAsync(client, includeDeleted: true);
        inclusive.Select(o => o.Id).Should().Contain([kept, removed], "?includeDeleted=true returns both figures for the same period");
    }

    // =============================================================================================================
    // Hard delete + its audit fact.
    // =============================================================================================================

    [Fact]
    public async Task HardDelete_ShouldRemoveTheRow_AndWriteAnAuditRowThatSatisfiesTheRealCheckConstraint()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid outcomeId = await SeedOwnedOutcomeAsync(userId);

        using HttpResponseMessage response = await client.DeleteAsync($"/outcomes/{outcomeId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse("hard delete removes the row");

        AuditRecord audit = await AuditRowForAsync(outcomeId);
        // Reaching the table at all proves CK_AuditRecords_Source_MatchesAction held for this row (a violation
        // would have thrown from SaveChangesAsync and turned the DELETE into a 500, not a 204).
        audit.Action.Should().Be(AuditAction.OutcomeHardDeleted);
        audit.Placement.Should().Be(AuditPlacement.None, "the removal concerns no protective leg");
        audit.Source.Should().BeNull("outside the kill/flatten set, CK_AuditRecords_Source_MatchesAction demands a null source");
        audit.UserId.Should().Be(userId, "the audit row is owned by the outcome's own owner (R-20)");
    }

    // =============================================================================================================
    // R-15 flag independence, end to end over HTTP.
    // =============================================================================================================

    [Fact]
    public async Task SoftDelete_ShouldMoveAllThreeFlagsTogether_ThroughTheEndpoint()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid outcomeId = await SeedOwnedOutcomeAsync(userId);

        using HttpResponseMessage response = await client.PostAsync($"/outcomes/{outcomeId}/soft-delete", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        OutcomeResponse? body = await response.Content.ReadFromJsonAsync<OutcomeResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(body);

        body.Deleted.Should().BeTrue();
        body.TrainingExcluded.Should().BeTrue();
        body.HiddenFromUser.Should().BeTrue();
    }

    [Fact]
    public async Task IndependentToggles_ShouldMoveOneFlagAtATime_ThroughTheEndpoint()
    {
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid userId = await OperatorUserIdAsync();
        Guid outcomeId = await SeedOwnedOutcomeAsync(userId);

        using HttpResponseMessage trainingResponse = await client.PutAsJsonAsync(
            $"/outcomes/{outcomeId}/training-exclusion", new { value = true });
        OutcomeResponse? afterTraining = await trainingResponse.Content.ReadFromJsonAsync<OutcomeResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(afterTraining);
        afterTraining.TrainingExcluded.Should().BeTrue();
        afterTraining.HiddenFromUser.Should().BeFalse("training exclusion alone must not hide the row");
        afterTraining.Deleted.Should().BeFalse();

        using HttpResponseMessage visibilityResponse = await client.PutAsJsonAsync(
            $"/outcomes/{outcomeId}/visibility", new { value = true });
        OutcomeResponse? afterVisibility = await visibilityResponse.Content.ReadFromJsonAsync<OutcomeResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(afterVisibility);
        afterVisibility.HiddenFromUser.Should().BeTrue();
        afterVisibility.TrainingExcluded.Should().BeTrue("the earlier toggle's flag must survive, untouched by this one");
        afterVisibility.Deleted.Should().BeFalse("two independent toggles are still not a soft-delete");
    }

    // =============================================================================================================
    // Operators.
    // =============================================================================================================

    private async Task<HttpClient> AuthenticatedOperatorClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"outcomes-b-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OutcomesB-Pass123!", "Outcomes Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        Guid userId = await QueryDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return (client, userId);
    }

    private Task<Guid> OperatorUserIdAsync() => QueryDbAsync(db =>
        db.Users.Where(u => u.Email == PostgresApiFactory.OperatorEmail).Select(u => u.Id).SingleAsync());

    // =============================================================================================================
    // HTTP helpers.
    // =============================================================================================================

    private async Task<List<OutcomeResponse>> ListAsync(HttpClient client, bool? includeDeleted)
    {
        string query = includeDeleted is null ? string.Empty : $"?includeDeleted={includeDeleted.Value}";
        using HttpResponseMessage response = await client.GetAsync($"/outcomes{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        OutcomeListResponse? body = await response.Content.ReadFromJsonAsync<OutcomeListResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(body);
        return [.. body.Outcomes];
    }

    // =============================================================================================================
    // Fixture — outcomes have no create endpoint, so they are seeded straight through the DbContext, parented to a
    // real closed Trade (the FK the row under test actually carries).
    // =============================================================================================================

    private async Task<Guid> SeedOwnedOutcomeAsync(Guid owner, bool softDeleted = false)
    {
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId);
        Guid outcomeId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            Outcome outcome = new()
            {
                Id = outcomeId,
                UserId = owner,
                TradeId = tradeId,
                Resolution = OutcomeResolution.Win,
                Simulated = false,
            };
            if (softDeleted)
            {
                outcome.SoftDelete();
            }

            db.Outcomes.Add(outcome);
            await db.SaveChangesAsync();
        });

        return outcomeId;
    }

    private async Task<Guid> SeedAccountAsync(Guid owner)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
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
        });

        return accountId;
    }

    private async Task<Guid> SeedClosedTradeAsync(Guid owner, Guid accountId)
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
                ExitPrice = 5_100m,
                RealizedPnL = 100m,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return tradeId;
    }

    private async Task<bool> OutcomeExistsAsync(Guid id) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().AnyAsync(o => o.Id == id));

    private async Task<bool> OutcomeStillOwnedByAsync(Guid id, Guid owner) => await QueryDbAsync(async db =>
    {
        Outcome? outcome = await db.Outcomes.IgnoreQueryFilters().SingleOrDefaultAsync(o => o.Id == id);
        return outcome is not null && outcome.UserId == owner && !outcome.Deleted;
    });

    /// <summary>Whether any audit row's <c>Detail</c> names this specific outcome id — scoped rather than a class-wide
    /// count, since the shared container-per-class fixture can carry another case's unrelated hard-delete row.</summary>
    private Task<bool> AuditRowMentionsAsync(Guid outcomeId) => QueryDbAsync(db => db.AuditRecords
        .IgnoreQueryFilters()
        .Where(row => row.Action == AuditAction.OutcomeHardDeleted)
        .AnyAsync(row => row.Detail != null && row.Detail.Contains(outcomeId.ToString())));

    /// <summary>The audit row this specific hard delete wrote — scoped to the outcome id, for the same reason.</summary>
    private Task<AuditRecord> AuditRowForAsync(Guid outcomeId) => QueryDbAsync(async db =>
    {
        List<AuditRecord> rows = await db.AuditRecords.IgnoreQueryFilters()
            .Where(row => row.Action == AuditAction.OutcomeHardDeleted
                && row.Detail != null && row.Detail.Contains(outcomeId.ToString()))
            .ToListAsync();
        return rows.Should().ContainSingle("exactly one hard delete has happened for this outcome").Subject;
    });

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }
}
