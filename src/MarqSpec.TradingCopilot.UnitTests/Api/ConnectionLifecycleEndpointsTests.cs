using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Connection lifecycle (gh#210, R-17, R-20, ADR-0015): credential rotation and soft-delete — the two endpoints
/// gh#160 found the gh#142 QA suite had been written against but which did not exist.
/// </summary>
/// <remarks>
/// Both are operator-scoped and default-deny: another operator's connection is <b>404, not 403</b> — a query that
/// forgets its scope must return nothing rather than admit the row exists (R-20). Deletion is a
/// <b>deactivation</b>, never a removal: the journal referencing those accounts is the audit trail, and losing it
/// would be invisible until an audit needed the rows.
/// </remarks>
public class ConnectionLifecycleEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(asUser ?? _operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> OptionsFor(string credentialKey) =>
        Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey });

    private async Task<Guid> SeedConnectionWithAccountAsync(string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-9001",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        await context.SaveChangesAsync();

        return connectionId;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Credential rotation — PUT /connections/{id}/credentials
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RotateCredentials_ShouldUpdateTheKey_WhenTheProcessHoldsIt()
    {
        Guid connectionId = await SeedConnectionWithAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.RotateCredentialsAsync(
            connectionId,
            new RotateCredentialsRequest("topstep-rotated"),
            new FixedUser(_operator),
            context,
            OptionsFor("topstep-rotated"),
            CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);

        await using TradingCopilotDbContext verify = Context();
        Connection rotated = await verify.Connections.SingleAsync(c => c.Id == connectionId);
        rotated.CredentialKey.Should().Be("topstep-rotated");
    }

    [Fact]
    public async Task RotateCredentials_ShouldRefuse_WhenTheProcessDoesNotHoldTheNewKey()
    {
        // Rotating to a key this process cannot serve would leave the connection pointing at an unusable
        // credential set -- discovery and every send would then fail at the venue instead of here (ADR-0015).
        Guid connectionId = await SeedConnectionWithAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.RotateCredentialsAsync(
            connectionId,
            new RotateCredentialsRequest("some-other-login"),
            new FixedUser(_operator),
            context,
            OptionsFor("topstep-main"),
            CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);

        await using TradingCopilotDbContext verify = Context();
        (await verify.Connections.SingleAsync(c => c.Id == connectionId)).CredentialKey.Should().Be("topstep-main",
            "a refused rotation must leave the connection on its working key");
    }

    [Fact]
    public async Task RotateCredentials_ShouldRefuseABlankKey_WhenTheRequestOmitsIt()
    {
        Guid connectionId = await SeedConnectionWithAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.RotateCredentialsAsync(
            connectionId, new RotateCredentialsRequest("  "), new FixedUser(_operator), context,
            OptionsFor("topstep-main"), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RotateCredentials_ShouldReturnNotFound_WhenTheConnectionBelongsToAnotherOperator()
    {
        // 404, never 403: default-deny returns NOTHING, so a probe cannot even learn the connection exists (R-20).
        Guid connectionId = await SeedConnectionWithAccountAsync();
        Guid stranger = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(stranger);

        IResult result = await ConnectionEndpoints.RotateCredentialsAsync(
            connectionId, new RotateCredentialsRequest("topstep-main"), new FixedUser(stranger), context,
            OptionsFor("topstep-main"), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Soft-delete — DELETE /connections/{id}
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeactivateConnection_ShouldDeactivateRatherThanRemove_AndCascadeToAccounts()
    {
        Guid connectionId = await SeedConnectionWithAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await ConnectionEndpoints.DeactivateConnectionAsync(
            connectionId, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);

        await using TradingCopilotDbContext verify = Context();
        Connection connection = await verify.Connections.SingleAsync(c => c.Id == connectionId);
        connection.IsActive.Should().BeFalse("deactivation, not removal");

        List<Account> accounts = await verify.Accounts.Where(a => a.ConnectionId == connectionId).ToListAsync();
        accounts.Should().NotBeEmpty("the accounts must still exist -- this is a soft delete");
        accounts.Should().OnlyContain(a => !a.IsActive, "deactivation cascades to the child accounts");
    }

    [Fact]
    public async Task DeactivateConnection_ShouldLeaveTheJournalIntact_WhenTheConnectionIsDeactivated()
    {
        // The whole reason this is a soft delete. An order/gate-decision row losing its account would be an
        // invisible failure -- nothing breaks until an audit needs the history, by which time it is gone.
        Guid connectionId = await SeedConnectionWithAccountAsync();
        Guid accountId;
        Guid orderId = Guid.NewGuid();

        await using (TradingCopilotDbContext seed = Context())
        {
            accountId = (await seed.Accounts.SingleAsync(a => a.ConnectionId == connectionId)).Id;
            seed.Orders.Add(new Order
            {
                Id = orderId,
                UserId = _operator,
                AccountId = accountId,
                Instrument = "MESM25",
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (TradingCopilotDbContext context = Context())
        {
            await ConnectionEndpoints.DeactivateConnectionAsync(
                connectionId, new FixedUser(_operator), context, CancellationToken.None);
        }

        await using TradingCopilotDbContext verify = Context();
        (await verify.Orders.AnyAsync(o => o.Id == orderId)).Should().BeTrue("the journal must survive a soft delete");
        (await verify.Accounts.AnyAsync(a => a.Id == accountId)).Should().BeTrue();
    }

    [Fact]
    public async Task DeactivateConnection_ShouldBeIdempotent_WhenCalledTwice()
    {
        Guid connectionId = await SeedConnectionWithAccountAsync();

        await using (TradingCopilotDbContext first = Context())
        {
            StatusOf(await ConnectionEndpoints.DeactivateConnectionAsync(
                connectionId, new FixedUser(_operator), first, CancellationToken.None)).Should().Be(StatusCodes.Status200OK);
        }

        await using TradingCopilotDbContext second = Context();
        StatusOf(await ConnectionEndpoints.DeactivateConnectionAsync(
            connectionId, new FixedUser(_operator), second, CancellationToken.None))
            .Should().Be(StatusCodes.Status200OK, "a second deactivation is a no-op, not an error");
    }

    [Fact]
    public async Task DeactivateConnection_ShouldReturnNotFound_WhenTheConnectionBelongsToAnotherOperator()
    {
        Guid connectionId = await SeedConnectionWithAccountAsync();
        Guid stranger = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(stranger);

        IResult result = await ConnectionEndpoints.DeactivateConnectionAsync(
            connectionId, new FixedUser(stranger), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);

        await using TradingCopilotDbContext verify = Context();
        (await verify.Connections.SingleAsync(c => c.Id == connectionId)).IsActive
            .Should().BeTrue("a stranger's call must not deactivate the owner's connection");
    }

    [Fact]
    public async Task DiscoverAccounts_ShouldRefuse_WhenTheConnectionIsDeactivated()
    {
        // A deactivated connection must stop being a live route to the venue -- otherwise "deleted" is cosmetic.
        Guid connectionId = await SeedConnectionWithAccountAsync();

        await using (TradingCopilotDbContext deactivate = Context())
        {
            await ConnectionEndpoints.DeactivateConnectionAsync(
                connectionId, new FixedUser(_operator), deactivate, CancellationToken.None);
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await ConnectionEndpoints.RotateCredentialsAsync(
            connectionId, new RotateCredentialsRequest("topstep-main"), new FixedUser(_operator), context,
            OptionsFor("topstep-main"), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict,
            "a deactivated connection is not a usable path -- reactivation is its own deliberate action");
    }
}
