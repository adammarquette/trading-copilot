using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The per-account stage override (gh#76) — the operator's correction on top of the conservative name
/// resolver. The safety-relevant behaviours: <c>Unknown</c> and undefined stages are refused (declare a real
/// stage or clear — the fail-open lesson), the resolver's own answer is never touched, and clearing falls back
/// to it.
/// </summary>
public class AccountEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(_operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    /// <summary>Seeds firm (Practice=paper, Funded=capital-at-risk) → connection → one account.</summary>
    private async Task<Guid> SeedAccountAsync(AccountStage resolvedStage, AccountStage? existingOverride = null)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
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
            CredentialKey = "topstep-main",
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "50KTC-V2-1234",
            Stage = resolvedStage,
            StageOverride = existingOverride,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task SetStageOverride_ShouldPersistTheDeclaration_AndComputeTheModeFromIt()
    {
        Guid accountId = await SeedAccountAsync(resolvedStage: AccountStage.Unknown);
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.SetStageOverrideAsync(
            accountId, new SetStageOverrideRequest(AccountStage.Funded), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        AccountResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<AccountResponse>().Subject;
        response.StageOverride.Should().Be(AccountStage.Funded);
        response.Stage.Should().Be(AccountStage.Unknown); // the resolver's own answer is never touched
        response.Mode.Should().Be(TradingMode.Live);      // Funded is declared capital-at-risk at this firm

        await using TradingCopilotDbContext reload = Context();
        Account stored = await reload.Accounts.SingleAsync();
        stored.StageOverride.Should().Be(AccountStage.Funded);
        stored.Stage.Should().Be(AccountStage.Unknown);
    }

    [Fact]
    public async Task SetStageOverride_ShouldRefuseUnknown()
    {
        Guid accountId = await SeedAccountAsync(resolvedStage: AccountStage.Practice);
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.SetStageOverrideAsync(
            accountId, new SetStageOverrideRequest(AccountStage.Unknown), context, CancellationToken.None);

        // "I don't know" is not a declaration -- clearing the override is how you say that.
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Accounts.SingleAsync()).StageOverride.Should().BeNull();
    }

    [Fact]
    public async Task SetStageOverride_ShouldRefuseAStageOutsideTheEnum()
    {
        Guid accountId = await SeedAccountAsync(resolvedStage: AccountStage.Practice);
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.SetStageOverrideAsync(
            accountId, new SetStageOverrideRequest((AccountStage)99), context, CancellationToken.None);

        // Whitelist, not blacklist (the fail-open lesson): only defined, declarable stages pass.
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Accounts.SingleAsync()).StageOverride.Should().BeNull();
    }

    [Fact]
    public async Task SetStageOverride_ShouldReturnNotFound_ForAnUnknownAccount()
    {
        await SeedAccountAsync(resolvedStage: AccountStage.Practice);
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.SetStageOverrideAsync(
            Guid.NewGuid(), new SetStageOverrideRequest(AccountStage.Funded), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ClearStageOverride_ShouldFallBackToTheResolvedStage()
    {
        Guid accountId = await SeedAccountAsync(resolvedStage: AccountStage.Practice, existingOverride: AccountStage.Funded);
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.ClearStageOverrideAsync(accountId, context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        AccountResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<AccountResponse>().Subject;
        response.StageOverride.Should().BeNull();
        response.Mode.Should().Be(TradingMode.Practice); // computed from the resolver's Practice again

        await using TradingCopilotDbContext reload = Context();
        (await reload.Accounts.SingleAsync()).StageOverride.Should().BeNull();
    }

    [Fact]
    public async Task ClearStageOverride_ShouldReturnNotFound_ForAnUnknownAccount()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await AccountEndpoints.ClearStageOverrideAsync(Guid.NewGuid(), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }
}
