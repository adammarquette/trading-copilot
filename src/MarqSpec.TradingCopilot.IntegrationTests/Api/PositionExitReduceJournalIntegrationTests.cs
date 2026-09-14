using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Blind, endpoint-level coverage for the per-position <b>exit</b> and <b>reduce</b> journal (gh#1162 of gh#1143;
/// R-8 / R-9 / R-11 / R-20, ADR-0001, ADR-0007 2026-09-06). Drives
/// <c>POST /accounts/{id}/positions/{instrument}/exit</c> and <c>…/reduce</c> through the real host against real
/// Postgres. Authored from the requirement, not from <c>PositionActionJournal</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt must leave one <c>position.exit</c> / <c>position.reduce</c> event (source <c>position-action</c>)
/// <b>and</b> one operator-owned <c>AuditRecord</c> (actions 9 / 10, <c>CK_AuditRecords_Source_MatchesAction</c>
/// with a null source). Every outcome leaves a row — refusals, <c>AccountBusy</c>, <c>HeldPracticeOnly</c>,
/// <c>Unreachable</c> — not only success. An exposure that was never established is recorded as unknown, never
/// <c>0</c>. The R-20 404 path writes nothing.
/// </para>
/// <para>
/// <b>Fixture pre-mortem — what this host can and cannot produce.</b>
/// </para>
/// <list type="table">
/// <listheader>
/// <term>Scenario</term>
/// <term>Reachable?</term>
/// </listheader>
/// <item>
/// <term>Exit Flat / StillOpen / Unreachable</term>
/// <term>Covered — default close flats; <c>MakeCloseIneffective</c> keeps the position open; <c>MakeCloseThrow</c>
/// is a hard venue rejection.</term>
/// </item>
/// <item>
/// <term>Reduce Reduced / Unconfirmed / NotReduced / Refused</term>
/// <term>Covered — the stub <b>feeds</b> the remaining net or a <see cref="VenueRefusalException"/>; it never
/// computes before − asked.</term>
/// </item>
/// <item>
/// <term>Reduce ExceedsPosition / HeldPracticeOnly / AccountBusy</term>
/// <term>Covered — a request at the open size; a funded account; a held <see cref="IAccountEntryGuard"/> lock.
/// <c>HeldPracticeOnly</c> is reachable because the reduce is practice-only until gh#1012 / ProjectX#98.</term>
/// </item>
/// <item>
/// <term>Reduce Unreachable</term>
/// <term>Covered — <c>MakeAccountUnreadable</c> faults the before-read so no exposure is established.</term>
/// </item>
/// <item>
/// <term>R-17 <c>NotSupportedException</c> / caller abort</term>
/// <term>Cannot happen here as a journaled outcome — ADR-0007 names both as paths that propagate rather than
/// journal. The stub grants <see cref="VenueCapability.ReducePosition"/> only when a reduce is meant to run.</term>
/// </item>
/// </list>
/// </remarks>
public class PositionExitReduceJournalIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private const string PracticeVenueKey = "PRAC-50K-101";
    private const string FundedVenueKey = "EXPRESS-50K-303";
    private const string Instrument = "ES";
    private const string Contract = "ESM25";
    private const string ExitEventType = "position.exit";
    private const string ReduceEventType = "position.reduce";
    private const string EventSource = "position-action";

    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public PositionExitReduceJournalIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Exit — Flat, StillOpen, Unreachable. Each attempt is one event + one audit row.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exit_ShouldLeaveOnePositionExitEventAndOneNullSourceAuditRow_WhenTheVenueReportsFlat()
    {
        // Prove-red: deleting the journal site leaves zero rows of either kind.
        AccountFixture account = await FreshPracticeAccountAsync();
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 2);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/exit", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a verified flat is the one successful exit");
        PositionExitResponse body = await ReadAsync<PositionExitResponse>(response);
        body.Outcome.Should().Be("Flat");

        await AssertJournaledAsync(
            baseline,
            ExitEventType,
            AuditAction.PositionExitAttempted,
            account.OwnerId,
            expectedOutcome: "Flat");
    }

    [Fact]
    public async Task Exit_ShouldStillLeaveARow_WhenTheVenueKeepsThePositionOpen()
    {
        // The refusals are what an incident is reconstructed from — a success-only journal would go green here
        // with zero rows.
        AccountFixture account = await FreshPracticeAccountAsync();
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 2);
        VenueFactory.MakeCloseIneffective(Contract);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/exit", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionExitResponse body = await ReadAsync<PositionExitResponse>(response);
        body.Outcome.Should().Be("StillOpen");

        await AssertJournaledAsync(
            baseline,
            ExitEventType,
            AuditAction.PositionExitAttempted,
            account.OwnerId,
            expectedOutcome: "StillOpen");
    }

    [Fact]
    public async Task Exit_ShouldRecordUnknownNeverZero_WhenTheVenueCannotBeReached()
    {
        // /exit still answers 0 on Unreachable (the wire divergence gh#928 documents). The journal is what an
        // incident reads back, and a 0 there fabricates a flat out of an outage (gh#929).
        AccountFixture account = await FreshPracticeAccountAsync();
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 2);
        VenueFactory.MakeCloseThrow(Contract);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/exit", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionExitResponse body = await ReadAsync<PositionExitResponse>(response);
        body.Outcome.Should().Be("Unreachable");
        body.NetQuantity.Should().Be(0, "the wire still answers 0 — that is not the journal's job");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ExitEventType,
            AuditAction.PositionExitAttempted,
            account.OwnerId,
            expectedOutcome: "Unreachable");
        AssertUnknownExposure(pair, "an unreachable exit must not write a 0 that would read as flat");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reduce — requested quantity, every named outcome, unknown never 0.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reduce_ShouldCarryTheRequestedQuantity_OnAVerifiedReduction()
    {
        // How many contracts were asked for is not reconstructable from venue truth afterwards (gh#1143).
        const int asked = 3;
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        VenueFactory.SeedReduceRemaining(Contract, remainingNet: 2);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(asked));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("Reduced");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "Reduced");
        AssertRequestedQuantity(pair, asked);
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenTheRequestExceedsTheOpenSize()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 2);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("ExceedsPosition");
        VenueFactory.ReducePositionCalls.Should().BeEmpty(
            "a request at or beyond the open size is refused before the venue is touched");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "ExceedsPosition");
        AssertRequestedQuantity(pair, 2);
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenTheAccountIsHeldPracticeOnly()
    {
        AccountFixture account = await FreshFundedAccountAsync();
        account.Mode.Should().NotBe(
            TradingMode.Practice, "the fixture must actually be able to produce HeldPracticeOnly");
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(1));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("HeldPracticeOnly");
        VenueFactory.ReducePositionCalls.Should().BeEmpty("a held reduce sends nothing");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "HeldPracticeOnly");
        AssertRequestedQuantity(pair, 1);
        AssertUnknownExposure(pair, "a hold that never read the position has no established exposure");
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenAnotherTransmitHoldsTheAccountLock()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        VenueFactory.SeedReduceRemaining(Contract, remainingNet: 3);
        long baseline = await LatestEventSequenceAsync();

        await using AsyncServiceScope holdScope = _factory.Services.CreateAsyncScope();
        IAccountEntryGuard guard = holdScope.ServiceProvider.GetRequiredService<IAccountEntryGuard>();
        TradingCopilotDbContext holdDb = holdScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        TaskCompletionSource entered = new();
        TaskCompletionSource release = new();
        Task<int> hold = guard.RunExclusiveAsync(
            holdDb,
            account.Id,
            async () =>
            {
                entered.SetResult();
                await release.Task;
                return 0;
            },
            CancellationToken.None);

        await entered.Task;
        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));
        release.SetResult();
        await hold;

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("AccountBusy");
        VenueFactory.ReducePositionCalls.Should().BeEmpty("a busy account sends nothing");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "AccountBusy");
        AssertRequestedQuantity(pair, 2);
        AssertUnknownExposure(pair, "a busy account never established exposure");
    }

    [Fact]
    public async Task Reduce_ShouldRecordUnknownNeverZero_WhenTheVenueCannotBeRead()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        VenueFactory.MakeAccountUnreadable(account.VenueKey);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("Unreachable");
        body.NetQuantity.Should().BeNull("the wire already refuses to render an outage as flat");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "Unreachable");
        AssertRequestedQuantity(pair, 2);
        AssertUnknownExposure(pair, "an unread exposure must stay unknown, never 0");
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenTheVenueDefinitivelyRefuses()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        VenueFactory.MakeReduceThrow(() =>
            new VenueRefusalException("venue refused the sized close", VenueRefusalKind.Definitive));
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("Refused");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "Refused");
        AssertRequestedQuantity(pair, 2);
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenTheVenueStillReportsTheOriginalSize()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        // No SeedReduceRemaining: the stub echoes the seeded open size — Unconfirmed, not a computed miss.
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("Unconfirmed");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "Unconfirmed");
        AssertRequestedQuantity(pair, 2);
    }

    [Fact]
    public async Task Reduce_ShouldLeaveARow_WhenThePositionMovedByTheWrongAmount()
    {
        AccountFixture account = await FreshPracticeAccountAsync(grantReduce: true);
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 5);
        VenueFactory.SeedReduceRemaining(Contract, remainingNet: 1);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        PositionReduceResponse body = await ReadAsync<PositionReduceResponse>(response);
        body.Outcome.Should().Be("NotReduced");

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ReduceEventType,
            AuditAction.PositionReduceAttempted,
            account.OwnerId,
            expectedOutcome: "NotReduced");
        AssertRequestedQuantity(pair, 2);
    }

    // ---------------------------------------------------------------------------------------------------------
    // R-20 — owner-scoped, and a 404 writes nothing.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditRow_ShouldBeInvisibleToAnotherOperator()
    {
        AccountFixture account = await FreshPracticeAccountAsync();
        VenueFactory.SeedPosition(account.VenueKey, Contract, netQuantity: 2);
        long baseline = await LatestEventSequenceAsync();

        using HttpResponseMessage response = await account.Client.PostAsync(
            $"/accounts/{account.Id}/positions/{Instrument}/exit", content: null);
        response.EnsureSuccessStatusCode();

        JournalPair pair = await AssertJournaledAsync(
            baseline,
            ExitEventType,
            AuditAction.PositionExitAttempted,
            account.OwnerId,
            expectedOutcome: "Flat");

        List<Guid> visibleToOwner = await VisibleAuditIdsAsync(account.OwnerId);
        visibleToOwner.Should().Contain(
            pair.Audit.Id, "the operator who asked for the exit can read their own audit row");

        Guid stranger = Guid.NewGuid();
        List<Guid> visibleToStranger = await VisibleAuditIdsAsync(stranger);
        visibleToStranger.Should().NotContain(
            pair.Audit.Id, "a second operator's context must not surface the row — R-20 default-deny");
    }

    [Fact]
    public async Task ExitAndReduce_ShouldWriteNothing_WhenTheAccountIsNotOwned()
    {
        AccountFixture owner = await FreshPracticeAccountAsync();
        VenueFactory.SeedPosition(owner.VenueKey, Contract, netQuantity: 5);
        VenueFactory.MakeReducePositionSupported();
        VenueFactory.SeedReduceRemaining(Contract, remainingNet: 3);
        HttpClient stranger = await CreateSecondOperatorAsync(owner.Client);
        long baseline = await LatestEventSequenceAsync();
        int auditsBefore = await AuditCountAsync();

        using HttpResponseMessage exit = await stranger.PostAsync(
            $"/accounts/{owner.Id}/positions/{Instrument}/exit", content: null);
        using HttpResponseMessage reduce = await stranger.PostAsJsonAsync(
            $"/accounts/{owner.Id}/positions/{Instrument}/reduce",
            new PositionReduceRequest(2));

        exit.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20: the account does not exist for this operator");
        reduce.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await EventsAfterAsync(baseline)).Should().BeEmpty(
            "a 404 has no owner to stamp and a row would be an existence side channel");
        (await AuditCountAsync()).Should().Be(auditsBefore, "the 404 path writes no AuditRecord either");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fixture.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<AccountFixture> FreshPracticeAccountAsync(bool grantReduce = false)
    {
        AccountFixture account = await FreshAccountAsync(
            PracticeVenueKey,
            new DeclareConventionsRequest(
            [
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true),
            ]));
        account.Mode.Should().Be(TradingMode.Practice, "the practice fixture must actually be Practice");
        VenueFactory.ReportRosterMode(PracticeVenueKey, TradingMode.Practice);
        if (grantReduce)
        {
            VenueFactory.MakeReducePositionSupported();
        }

        return account;
    }

    private async Task<AccountFixture> FreshFundedAccountAsync()
    {
        AccountFixture account = await FreshAccountAsync(
            FundedVenueKey,
            new DeclareConventionsRequest(
            [
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true),
            ]));
        account.Mode.Should().Be(TradingMode.Live, "the funded fixture must actually be Live so HeldPracticeOnly is reachable");
        return account;
    }

    private async Task<AccountFixture> FreshAccountAsync(string venueKey, DeclareConventionsRequest conventions)
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-PosJournal-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declared = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions", conventions);
        declared.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse chosen = accounts.Should().Contain(
            candidate => candidate.VenueAccountKey == venueKey, "discovery must surface the seeded venue account")
            .Which;
        Guid ownerId = await QueryDbAsync(db => db.Accounts
            .IgnoreQueryFilters()
            .Where(row => row.Id == chosen.Id)
            .Select(row => row.UserId)
            .SingleAsync());

        return new AccountFixture(client, chosen.Id, chosen.VenueAccountKey, chosen.Mode, ownerId);
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

    private async Task<HttpClient> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"operator-b-{Guid.NewGuid():N}@example.com";
        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync(
            "/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK);
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OperatorB-Pass123!", "Operator B"));
        accept.EnsureSuccessStatusCode();
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    private async Task<JournalPair> AssertJournaledAsync(
        long baseline,
        string eventType,
        AuditAction action,
        Guid ownerId,
        string expectedOutcome)
    {
        List<Event> events = (await EventsAfterAsync(baseline))
            .Where(candidate => candidate.Type == eventType)
            .ToList();
        events.Should().ContainSingle(
            $"each attempt leaves exactly one {eventType} event — a missing row is the defect this suite exists to catch")
            .Which.Source.Should().Be(EventSource, "ADR-0007: source is position-action");

        Event logged = events.Single();
        using JsonDocument payload = JsonDocument.Parse(logged.Payload);
        string outcome = ReadString(payload.RootElement, "outcome", "Outcome");
        outcome.Should().Be(expectedOutcome, "the durable event names the outcome the operator was told");

        List<AuditRecord> rows = await QueryDbAsync(db => db.AuditRecords
            .IgnoreQueryFilters()
            .Where(row => row.Action == action && row.UserId == ownerId)
            .OrderBy(row => row.RecordedAt)
            .ThenBy(row => row.Id)
            .ToListAsync());
        AuditRecord audit = rows.Should().ContainSingle(
            $"each attempt leaves exactly one {action} audit row for this operator")
            .Which;

        audit.Source.Should().BeNull(
            "actions 9/10 sit outside the kill/flatten set — CK_AuditRecords_Source_MatchesAction admits a null source");
        audit.Placement.Should().Be(AuditPlacement.None, "a position action rests on no single protective leg");
        audit.SyntheticRisk.Should().BeFalse();
        audit.After.Should().Be(expectedOutcome, "After carries the outcome, the auto-flatten's shape");
        audit.UserId.Should().Be(ownerId, "the row is stamped with the operator who asked");

        return new JournalPair(logged, audit, payload.RootElement.Clone());
    }

    private static void AssertRequestedQuantity(JournalPair pair, int asked)
    {
        int? fromPayload = ReadOptionalInt(
            pair.Payload, "requestedQuantity", "RequestedQuantity", "quantity", "Quantity");
        fromPayload.Should().Be(
            asked, $"the event payload is the only reconstructable record of the asked-for size; payload={pair.Event.Payload}");
        pair.Audit.Detail.Should().Contain(
            asked.ToString(), "Detail is the prose line an incident reads for the requested quantity");
    }

    private static void AssertUnknownExposure(JournalPair pair, string because)
    {
        if (TryReadProperty(pair.Payload, out JsonElement after, "netQuantityAfter", "NetQuantityAfter"))
        {
            after.ValueKind.Should().Be(
                JsonValueKind.Null, $"{because}; a number here — especially 0 — fabricates a flat. payload={pair.Event.Payload}");
        }

        if (int.TryParse(pair.Audit.Before, out int before) && before == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{because}; AuditRecord.Before is 0, which would read as a flat out of an outage.");
        }
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        if (!TryReadProperty(root, out JsonElement value, names))
        {
            throw new Xunit.Sdk.XunitException(
                $"payload missing any of [{string.Join(", ", names)}]: {root}");
        }

        return value.GetString() ?? throw new Xunit.Sdk.XunitException(
            $"payload property {names[0]} was null: {root}");
    }

    private static int? ReadOptionalInt(JsonElement root, params string[] names)
    {
        if (!TryReadProperty(root, out JsonElement value, names) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetInt32();
    }

    private static bool TryReadProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        T? body = await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
        return body ?? throw new InvalidOperationException($"response body did not deserialize to {typeof(T).Name}");
    }

    private Task<long> LatestEventSequenceAsync() => QueryDbAsync(async db =>
        await db.Events.AnyAsync() ? await db.Events.MaxAsync(evt => evt.Sequence) : 0L);

    private Task<List<Event>> EventsAfterAsync(long baseline) => QueryDbAsync(db => db.Events
        .AsNoTracking()
        .Where(evt => evt.Sequence > baseline)
        .OrderBy(evt => evt.Sequence)
        .ToListAsync());

    private Task<int> AuditCountAsync() =>
        QueryDbAsync(db => db.AuditRecords.IgnoreQueryFilters().CountAsync());

    private async Task<List<Guid>> VisibleAuditIdsAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext db = new(options, new OwnerUser(userId));
        return await db.AuditRecords.Select(row => row.Id).ToListAsync();
    }

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

    private sealed record AccountFixture(
        HttpClient Client, Guid Id, string VenueKey, TradingMode Mode, Guid OwnerId);

    private sealed record JournalPair(Event Event, AuditRecord Audit, JsonElement Payload);
}
