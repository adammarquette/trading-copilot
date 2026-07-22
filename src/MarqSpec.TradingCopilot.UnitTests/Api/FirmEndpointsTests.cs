using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>/firms</c> configuration handlers (gh#76). Covers what the operator can declare and — because these
/// rows are the R-14 input — what the handlers <b>refuse</b> to store.
/// </summary>
public class FirmEndpointsTests
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

    private async Task<Guid> SeedFirmAsync(string name = "Topstep")
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await FirmEndpoints.CreateFirmAsync(
            new CreateFirmRequest(name, FirmType.PropFirm), new FixedUser(_operator), context, CancellationToken.None);
        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        return await context.Firms.Select(firm => firm.Id).SingleAsync();
    }

    [Fact]
    public async Task CreateFirm_ShouldStoreAFirmOwnedByTheOperator()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.CreateFirmAsync(
            new CreateFirmRequest("Apex", FirmType.PropFirm), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        Firm stored = await context.Firms.SingleAsync();
        stored.Name.Should().Be("Apex");
        stored.UserId.Should().Be(_operator);
    }

    [Fact]
    public async Task CreateFirm_ShouldRejectABlankName()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.CreateFirmAsync(
            new CreateFirmRequest("  ", FirmType.PropFirm), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await context.Firms.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateFirm_ShouldConflict_WhenTheOperatorAlreadyHasThatFirm()
    {
        await SeedFirmAsync("Topstep");
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.CreateFirmAsync(
            new CreateFirmRequest("Topstep", FirmType.PropFirm), new FixedUser(_operator), context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await context.Firms.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ListFirms_ShouldReturnOnlyTheOperatorsOwnFirms()
    {
        Guid other = Guid.NewGuid();
        await using (TradingCopilotDbContext asOther = Context(other))
        {
            await FirmEndpoints.CreateFirmAsync(
                new CreateFirmRequest("SomeoneElsesFirm", FirmType.Brokerage), new FixedUser(other), asOther, CancellationToken.None);
        }

        await SeedFirmAsync("MyFirm");

        await using TradingCopilotDbContext context = Context();
        IResult result = await FirmEndpoints.ListFirmsAsync(context, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        IReadOnlyList<FirmResponse> firms = ((IValueHttpResult)result).Value.Should().BeAssignableTo<IReadOnlyList<FirmResponse>>().Subject;
        firms.Should().ContainSingle(firm => firm.Name == "MyFirm");
    }

    [Fact]
    public async Task DeclareConventions_ShouldPersistThemAndResolveModes()
    {
        Guid firmId = await SeedFirmAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.DeclareConventionsAsync(
            firmId,
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, false),
                new StageConventionDto(AccountStage.Funded, true),
            ]),
            new FixedUser(_operator),
            context,
            CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);

        await using TradingCopilotDbContext reload = Context();
        Firm firm = await reload.Firms.Include(f => f.StageConventions).SingleAsync();
        firm.ToConventions().ModeFor(AccountStage.Funded).Should().Be(TradingMode.Live);
        firm.ToConventions().ModeFor(AccountStage.Practice).Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task DeclareConventions_ShouldReplaceThePreviousSet()
    {
        Guid firmId = await SeedFirmAsync();

        await using (TradingCopilotDbContext first = Context())
        {
            await FirmEndpoints.DeclareConventionsAsync(
                firmId, new DeclareConventionsRequest([new StageConventionDto(AccountStage.Funded, true)]),
                new FixedUser(_operator), first, CancellationToken.None);
        }

        await using (TradingCopilotDbContext second = Context())
        {
            await FirmEndpoints.DeclareConventionsAsync(
                firmId, new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, false)]),
                new FixedUser(_operator), second, CancellationToken.None);
        }

        await using TradingCopilotDbContext reload = Context();
        Firm firm = await reload.Firms.Include(f => f.StageConventions).SingleAsync();
        firm.StageConventions.Should().ContainSingle(convention => convention.Stage == AccountStage.Practice);
    }

    [Theory]
    [InlineData(AccountStage.Unknown, AccountStage.Funded)]  // Unknown is not declarable
    [InlineData(AccountStage.Funded, AccountStage.Funded)]   // duplicate stage
    public async Task DeclareConventions_ShouldRejectAnInvalidSet_AndPersistNothing(AccountStage first, AccountStage second)
    {
        Guid firmId = await SeedFirmAsync();
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.DeclareConventionsAsync(
            firmId,
            new DeclareConventionsRequest([new StageConventionDto(first, true), new StageConventionDto(second, false)]),
            new FixedUser(_operator),
            context,
            CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await context.FirmStageConventions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DeclareConventions_ShouldReturnNotFound_ForAnUnknownFirm()
    {
        await using TradingCopilotDbContext context = Context();

        IResult result = await FirmEndpoints.DeclareConventionsAsync(
            Guid.NewGuid(),
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Funded, true)]),
            new FixedUser(_operator),
            context,
            CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }
}
