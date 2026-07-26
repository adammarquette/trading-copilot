using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>/accounts/{id}/risk</c> handlers (gh#10, R-5) — the operator declares an account's risk rules; the
/// gate later composes from exactly what is persisted. The safety-relevant behaviours: an invalid declaration
/// is refused whole (never partially stored), one profile per account (redeclaration replaces), and an
/// undeclared account has no profile — the gate's fail-closed input, not a default.
/// </summary>
public class RiskEndpointsTests
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

    private static DeclareRiskProfileRequest ValidRequest() => new(
        DailyLossLimit: 1_000m,
        AccountProfitTarget: 3_000m,
        StartingBalance: 50_000m,
        FloorSource: FloorSource.FirmImposed,
        TrailingMode: TrailingMode.EndOfDay,
        TrailingAmount: 2_000m,
        LocksAt: 50_100m,
        PerTradeRiskFraction: 0.15m,
        TargetRewardRatio: 1.5m,
        MaxDrawdownPerTrade: 300m,
        DailyDrawdownGovernor: 600m,
        DailyProfitTarget: 1_500m,
        StopForDayAtProfitTarget: true,
        SizingBasis: SizingBasis.SafetyStop,
        MaxContractsPerOrder: 3,
        MaxBestDayFraction: 0.4m);

    private async Task<Guid> SeedAccountAsync(TradingMode mode = TradingMode.Practice)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
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
            Name = "150KTC-V2-1234",
            Stage = AccountStage.Evaluation,
            Mode = mode,
            Balance = 150_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task DeclareRisk_ShouldPersistTheProfile_OwnedAndOnePerAccount()
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId, ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        RiskProfileRecord stored = await reload.RiskProfiles.SingleAsync();
        stored.AccountId.Should().Be(accountId);
        stored.UserId.Should().Be(_operator);
        stored.TrailingAmount.Should().Be(2_000m);
        stored.PerTradeRiskFraction.Should().Be(0.15m);
        stored.MaxBestDayFraction.Should().Be(0.4m);
    }

    [Fact]
    public async Task DeclareRisk_ShouldReplaceOnRedeclaration_NeverDuplicate()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext first = Context())
        {
            await RiskEndpoints.DeclareRiskAsync(
                accountId, ValidRequest(), new FixedUser(_operator), first, CancellationToken.None);
        }

        await using TradingCopilotDbContext second = Context();
        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId,
            ValidRequest() with { TrailingAmount = 2_500m },
            new FixedUser(_operator), second, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        RiskProfileRecord stored = await reload.RiskProfiles.SingleAsync(); // one row, not two
        stored.TrailingAmount.Should().Be(2_500m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2000)]
    public async Task DeclareRisk_ShouldRefuseANonPositiveTrailingAmount_AndPersistNothing(decimal amount)
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId, ValidRequest() with { TrailingAmount = amount }, new FixedUser(_operator), context, CancellationToken.None);

        // Validated through the DOMAIN constructors (TrailingDrawdown.Start) -- one source of truth, and a bad
        // declaration is refused whole, never partially stored.
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        await using TradingCopilotDbContext reload = Context();
        (await reload.RiskProfiles.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    public async Task DeclareRisk_ShouldRefuseAPerTradeRiskFractionOutsideZeroToOne(decimal fraction)
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId, ValidRequest() with { PerTradeRiskFraction = fraction }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        await using TradingCopilotDbContext reload = Context();
        (await reload.RiskProfiles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DeclareRisk_ShouldRefuseANegativeManualCap()
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId, ValidRequest() with { MaxContractsPerOrder = -1 }, new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DeclareRisk_ShouldReturnNotFound_ForAnUnknownAccount()
    {
        await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            Guid.NewGuid(), ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetRisk_ShouldReturnTheDeclaredProfile()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext declare = Context())
        {
            await RiskEndpoints.DeclareRiskAsync(
                accountId, ValidRequest(), new FixedUser(_operator), declare, CancellationToken.None);
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await RiskEndpoints.GetRiskAsync(accountId, context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        RiskProfileResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<RiskProfileResponse>().Subject;
        response.TrailingMode.Should().Be(TrailingMode.EndOfDay);
        response.SizingBasis.Should().Be(SizingBasis.SafetyStop);
        response.DailyLossLimit.Should().Be(1_000m);
    }

    [Fact]
    public async Task GetRisk_ShouldReturnNotFound_WhenNothingIsDeclared()
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        // No default profile is invented: "undeclared" is the gate's fail-closed input (gh#10), and the API
        // says so plainly rather than fabricating permissive numbers.
        IResult result = await RiskEndpoints.GetRiskAsync(accountId, context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RiskProfiles_ShouldBeInvisible_ToAnotherOperator()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext declare = Context())
        {
            await RiskEndpoints.DeclareRiskAsync(
                accountId, ValidRequest(), new FixedUser(_operator), declare, CancellationToken.None);
        }

        // Default-deny (R-20): risk limits are the most consequential declaration in the workspace.
        await using TradingCopilotDbContext otherOperator = Context(asUser: Guid.NewGuid());
        (await otherOperator.RiskProfiles.AnyAsync()).Should().BeFalse();
    }

    // --- Default entry action preference (gh#218) ---

    [Fact]
    public async Task DeclareRisk_ShouldResolveToApproveAndArm_WhenNoPreferenceIsGiven()
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        // ValidRequest() leaves DefaultEntryAction defaulted -- review-first by construction, not a defaulting branch.
        await RiskEndpoints.DeclareRiskAsync(accountId, ValidRequest(), new FixedUser(_operator), context, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.RiskProfiles.SingleAsync()).DefaultEntryAction.Should().Be(DefaultEntryAction.ApproveAndArm);
    }

    [Fact]
    public async Task DeclareRisk_ShouldRefuseSendAsIsDefault_OnALiveAccount_NamingTheMode()
    {
        Guid accountId = await SeedAccountAsync(TradingMode.Live);
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId,
            ValidRequest() with { DefaultEntryAction = DefaultEntryAction.SendAsIs, ConfirmSendAsIsDefault = true },
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        ((IValueHttpResult)result).Value!.ToString().Should().Contain("Live"); // the refusal names the mode
        (await Context().RiskProfiles.AnyAsync()).Should().BeFalse();          // nothing persisted
    }

    [Fact]
    public async Task DeclareRisk_ShouldRefuseSendAsIsDefault_OnAnUndeclaredAccount()
    {
        Guid accountId = await SeedAccountAsync(TradingMode.Undeclared);
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId,
            ValidRequest() with { DefaultEntryAction = DefaultEntryAction.SendAsIs, ConfirmSendAsIsDefault = true },
            new FixedUser(_operator), context, CancellationToken.None);

        // Mode is declared, never derived (R-14, gh#60): undeclared trades nowhere and cannot default to send-as-is.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task DeclareRisk_ShouldRefuseSendAsIsDefault_OnAPracticeAccountWithoutConfirmation()
    {
        Guid accountId = await SeedAccountAsync(); // practice
        await using TradingCopilotDbContext context = Context();

        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId,
            ValidRequest() with { DefaultEntryAction = DefaultEntryAction.SendAsIs }, // ConfirmSendAsIsDefault defaults false
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await Context().RiskProfiles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DeclareRisk_ShouldPersistSendAsIsDefault_OnAPracticeAccountWithConfirmation_AndSurviveReload()
    {
        Guid accountId = await SeedAccountAsync(); // practice
        await using (TradingCopilotDbContext context = Context())
        {
            IResult result = await RiskEndpoints.DeclareRiskAsync(
                accountId,
                ValidRequest() with { DefaultEntryAction = DefaultEntryAction.SendAsIs, ConfirmSendAsIsDefault = true },
                new FixedUser(_operator), context, CancellationToken.None);
            StatusOf(result).Should().Be(StatusCodes.Status200OK);
        }

        // A fresh context reads it back from the store -- the unit-test analogue of surviving a restart.
        await using TradingCopilotDbContext reload = Context();
        (await reload.RiskProfiles.SingleAsync()).DefaultEntryAction.Should().Be(DefaultEntryAction.SendAsIs);
    }

    [Fact]
    public async Task DeclareRisk_ShouldNotRequireConfirmation_ToSetApproveAndArm()
    {
        Guid accountId = await SeedAccountAsync();
        await using TradingCopilotDbContext context = Context();

        // Setting the review-first default is always free -- no mode gate, no confirmation.
        IResult result = await RiskEndpoints.DeclareRiskAsync(
            accountId,
            ValidRequest() with { DefaultEntryAction = DefaultEntryAction.ApproveAndArm },
            new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task GetRisk_ShouldReturnTheDefaultEntryAction()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext declare = Context())
        {
            await RiskEndpoints.DeclareRiskAsync(
                accountId,
                ValidRequest() with { DefaultEntryAction = DefaultEntryAction.SendAsIs, ConfirmSendAsIsDefault = true },
                new FixedUser(_operator), declare, CancellationToken.None);
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await RiskEndpoints.GetRiskAsync(accountId, context, CancellationToken.None);

        RiskProfileResponse response = ((IValueHttpResult)result).Value.Should().BeOfType<RiskProfileResponse>().Subject;
        response.DefaultEntryAction.Should().Be(DefaultEntryAction.SendAsIs);
    }
}
