using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>/connections</c> handlers — firm logins and account discovery. The safety-relevant behaviours: a
/// credential-key mismatch is refused (one credential set per process, ADR-0015), an unwired platform is refused
/// at creation, and rediscovery updates rather than duplicates.
/// </summary>
public class ConnectionEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ConnectionEndpointsTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
    }

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(_operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> OptionsFor(string credentialKey) =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey });

    /// <summary>The R-14 environment the handlers run in — Development (practice-only) unless a test says otherwise.</summary>
    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    private async Task<(Guid FirmId, Guid ConnectionId)> SeedFirmAndConnectionAsync(string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = _operator,
            Name = "Topstep",
            Type = FirmType.PropFirm,
            StageConventions =
            [
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = _operator, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = _operator, FirmId = firmId, Stage = AccountStage.Funded, CapitalAtRisk = true },
            ],
        });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        await context.SaveChangesAsync();
        return (firmId, connectionId);
    }

    [Fact]
    public async Task CreateConnection_ShouldRefuseAnUnwiredPlatform()
    {
        (Guid firmId, _) = await SeedFirmAndConnectionAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.CreateConnectionAsync(
            new CreateConnectionRequest(firmId, "rithmic", "some-key"),
            new FixedUser(_operator), context, CancellationToken.None);

        // Shown-as-unavailable beats silently-accepted (ADR-0016): this connection could never discover or trade.
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateConnection_ShouldConflict_OnASecondLoginForTheSameFirmAndPlatform()
    {
        (Guid firmId, _) = await SeedFirmAndConnectionAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.CreateConnectionAsync(
            new CreateConnectionRequest(firmId, "projectx", "another-key"),
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await context.Connections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldRefuse_WhenThisProcessHoldsADifferentCredentialKey()
    {
        (_, Guid connectionId) = await SeedFirmAndConnectionAsync(credentialKey: "someone-elses-login");
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.DiscoverAccountsAsync(
            connectionId, new FixedUser(_operator), context, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);

        // One credential set per process (ADR-0015). Serving another key's connection would silently discover
        // the wrong login's accounts into this connection's rows.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldPersistWhatTheVenueReports_WithStageAndComputedMode()
    {
        (_, Guid connectionId) = await SeedFirmAndConnectionAsync();
        VenueId venueId = VenueId.Parse("projectx");
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(VenueAccountId.Create(venueId, "9001"), "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
            new VenueAccount(VenueAccountId.Create(venueId, "9002"), "50KTC-V2", 49_000m, CanTrade: true, IsVisible: true, TradingMode.Undeclared) { Stage = AccountStage.Unknown },
        ]);

        await using TradingCopilotDbContext context = Context();
        IResult result = await ConnectionEndpoints.DiscoverAccountsAsync(
            connectionId, new FixedUser(_operator), context, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);

        await using TradingCopilotDbContext reload = Context();
        List<Account> stored = await reload.Accounts.OrderBy(account => account.VenueAccountKey).ToListAsync();
        stored.Should().HaveCount(2);
        stored[0].Stage.Should().Be(AccountStage.Practice);
        stored[0].UserId.Should().Be(_operator);
        stored[1].Stage.Should().Be(AccountStage.Unknown); // unrecognised name stays Unknown -> Undeclared, never guessed

        // The PERSISTED mode (gh#7): written at discovery so the DB-level R-14 guard has a truth to compare
        // orders against. Must equal what the conventions compute -- staleness here is what the recompute-at-
        // every-write-point discipline prevents.
        stored[0].Mode.Should().Be(TradingMode.Practice);
        stored[1].Mode.Should().Be(TradingMode.Undeclared);

        // The R-14 environment gate at the account-selection boundary (gh#9): practice trades anywhere,
        // undeclared trades NOWHERE -- not even here in Development.
        List<AccountResponse> responses = (List<AccountResponse>)((IValueHttpResult)result).Value!;
        responses.Single(r => r.Stage == AccountStage.Practice).TradeableHere.Should().BeTrue();
        responses.Single(r => r.Stage == AccountStage.Unknown).TradeableHere.Should().BeFalse();
    }

    /// <summary>Seeds a BROKERAGE firm — no stage conventions, because a brokerage has no stage ladder.</summary>
    private async Task<Guid> SeedBrokerageConnectionAsync()
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = _operator,
            Name = "Interactive Brokers",
            Type = FirmType.Brokerage,
            StageConventions = [],
        });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        await context.SaveChangesAsync();
        return connectionId;
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldPersistABrokerageModeFromTheVenueFlag_NotUndeclared()
    {
        // gh#780, and the #907 review's second round. This is the wiring the first fix missed: resolving mode
        // correctly in FirmConventions changed nothing, because discovery persists via RecomputeMode and the
        // venue flag had no way to reach it. Proving it at ConnectionEndpoints -- not just at the domain method --
        // is what makes a future revert of that wiring fail here rather than silently ship.
        Guid connectionId = await SeedBrokerageConnectionAsync();
        VenueId venueId = VenueId.Parse("projectx");
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(VenueAccountId.Create(venueId, "U1000"), "PAPER", 25_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)
            {
                Stage = AccountStage.Unknown,
                VenueReportsSimulated = true,
            },
            new VenueAccount(VenueAccountId.Create(venueId, "U2000"), "LIVE", 75_000m, CanTrade: true, IsVisible: true, TradingMode.Live)
            {
                Stage = AccountStage.Unknown,
                VenueReportsSimulated = false,
            },
        ]);

        await using TradingCopilotDbContext context = Context();
        IResult result = await ConnectionEndpoints.DiscoverAccountsAsync(
            connectionId, new FixedUser(_operator), context, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);

        await using TradingCopilotDbContext reload = Context();
        List<Account> stored = await reload.Accounts.OrderBy(account => account.VenueAccountKey).ToListAsync();

        // Both accounts sit at AccountStage.Unknown -- a brokerage has no ladder -- so under the per-stage model
        // both persisted Undeclared and were tradeable nowhere. The venue's flag is what separates them.
        stored[0].Mode.Should().Be(TradingMode.Practice, "the venue reports U1000 as paper");
        stored[1].Mode.Should().Be(TradingMode.Live, "the venue reports U2000 as live");
        stored.Should().OnlyContain(account => account.Mode != TradingMode.Undeclared);

        // The flag itself is persisted, because the later recomputes (a stage override, a conventions
        // re-declaration) have no venue call of their own to ask again.
        stored[0].VenueReportsSimulated.Should().BeTrue();
        stored[1].VenueReportsSimulated.Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldUpsertOnRediscovery_NeverDuplicate()
    {
        (_, Guid connectionId) = await SeedFirmAndConnectionAsync();
        VenueId venueId = VenueId.Parse("projectx");
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(VenueAccountId.Create(venueId, "9001"), "PRAC-50K", 50_000m, true, true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);

        await using (TradingCopilotDbContext first = Context())
        {
            await ConnectionEndpoints.DiscoverAccountsAsync(
                connectionId, new FixedUser(_operator), first, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);
        }

        // The venue now reports a changed balance for the same handle.
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(VenueAccountId.Create(venueId, "9001"), "PRAC-50K", 51_250m, true, true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);

        await using (TradingCopilotDbContext second = Context())
        {
            await ConnectionEndpoints.DiscoverAccountsAsync(
                connectionId, new FixedUser(_operator), second, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);
        }

        await using TradingCopilotDbContext reload = Context();
        Account account = await reload.Accounts.SingleAsync(); // one row, not two
        account.Balance.Should().Be(51_250m);
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldPreserveTheOperatorOverride_AcrossRediscovery()
    {
        (_, Guid connectionId) = await SeedFirmAndConnectionAsync();
        VenueId venueId = VenueId.Parse("projectx");
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(VenueAccountId.Create(venueId, "9001"), "50KTC-V2", 49_000m, true, true, TradingMode.Undeclared) { Stage = AccountStage.Unknown },
        ]);

        await using (TradingCopilotDbContext first = Context())
        {
            await ConnectionEndpoints.DiscoverAccountsAsync(
                connectionId, new FixedUser(_operator), first, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);
        }

        // The operator corrects the resolver: this one is actually Funded.
        await using (TradingCopilotDbContext declare = Context())
        {
            Account account = await declare.Accounts.SingleAsync();
            account.StageOverride = AccountStage.Funded;
            await declare.SaveChangesAsync();
        }

        await using TradingCopilotDbContext second = Context();
        IResult result = await ConnectionEndpoints.DiscoverAccountsAsync(
            connectionId, new FixedUser(_operator), second, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        List<AccountResponse> responses = (List<AccountResponse>)((IValueHttpResult)result).Value!;
        responses.Single().StageOverride.Should().Be(AccountStage.Funded);
        responses.Single().Mode.Should().Be(TradingMode.Live); // computed from the OVERRIDE, not the resolver's Unknown

        await using TradingCopilotDbContext reload = Context();
        Account stored = await reload.Accounts.SingleAsync(); // still one row
        stored.StageOverride.Should().Be(AccountStage.Funded); // rediscovery refreshed the resolver's view...
        stored.Stage.Should().Be(AccountStage.Unknown);        // ...and never touched the declaration
    }

    [Fact]
    public async Task ListAccounts_ShouldReturnThePersistedRoster_WithoutCallingTheVenue()
    {
        (_, Guid connectionId) = await SeedFirmAndConnectionAsync();
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                ConnectionId = connectionId,
                VenueAccountKey = "9001",
                Name = "50KTC-V2",
                Stage = AccountStage.Unknown,
                StageOverride = AccountStage.Funded,
                Mode = TradingMode.Live, // what the override write point persisted (Funded x capital-at-risk)
                CanTrade = true,
                IsVisible = true,
                Balance = 49_000m,
            });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await ConnectionEndpoints.ListAccountsAsync(connectionId, context, Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        List<AccountResponse> responses = (List<AccountResponse>)((IValueHttpResult)result).Value!;
        responses.Single().Mode.Should().Be(TradingMode.Live); // served from the persisted column, no venue call
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ListAccounts_ShouldReturnNotFound_ForAnUnknownConnection()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.ListAccountsAsync(Guid.NewGuid(), context, Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldReturnNotFound_ForAnUnknownConnection()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.DiscoverAccountsAsync(
            Guid.NewGuid(), new FixedUser(_operator), context, _factory, OptionsFor("topstep-main"), Development, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }
}
