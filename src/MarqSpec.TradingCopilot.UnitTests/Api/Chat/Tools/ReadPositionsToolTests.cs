using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>read_positions</c> chat tool (gh#929): a read-only report of the operator's open positions from
/// <b>venue truth</b> across their accounts, tagged with each account's mark basis. The safety-relevant behaviours
/// are unit-testable behind an in-memory <see cref="TradingCopilotDbContext"/> (the R-20 account scoping) plus a
/// faked <see cref="IPositionReconciler"/> (the venue read itself is QA integration-tier): an unreachable venue is
/// reported <b>declared-unknown</b>, never a fabricated flat; a read fault fails closed; and the tool reaches no
/// order / exit path (it injects only the read seam).
/// </summary>
public class ReadPositionsToolTests
{
    private static readonly VenueId _venue = VenueId.Parse("projectx");

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly IPositionReconciler _reconciler = A.Fake<IPositionReconciler>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid asUser) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options, new FixedUser(asUser));

    private ReadPositionsTool Tool() => new(Context(_owner), _reconciler, NullLogger<ReadPositionsTool>.Instance);

    private static Account Owned(Guid id, string name, bool active = true, TradingMode mode = TradingMode.Practice) => new()
    {
        Id = id,
        VenueAccountKey = "9001",
        Name = name,
        Mode = mode,
        IsActive = active,
    };

    private async Task SeedAsync(Guid owner, params Account[] accounts)
    {
        await using TradingCopilotDbContext context = Context(owner);
        foreach (Account account in accounts)
        {
            account.UserId = owner; // the owning operator (R-20) the tenant filter reads back against
        }

        context.Accounts.AddRange(accounts);
        await context.SaveChangesAsync();
    }

    private void Reconciles(Guid accountId, PositionReconciliation result) =>
        A.CallTo(() => _reconciler.ReconcileAsync(accountId, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(result);

    private static PositionSnapshot Position(string contract, int net) =>
        new(VenueAccountId.Create(_venue, "9001"), VenueContractId.Create(_venue, contract), net, new Price(5_300m));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Definition_ShouldBeNamedReadPositions_WithAnObjectSchema()
    {
        IChatTool tool = Tool();

        tool.Name.Should().Be("read_positions");
        tool.Definition.Name.Should().Be("read_positions");
        tool.Definition.Description.Should().NotBeNullOrWhiteSpace();
        Parse(tool.Definition.InputSchema).GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportOpenPositionsFromVenueTruth_TaggedWithMarkBasis()
    {
        Guid accountId = Guid.NewGuid();
        await SeedAsync(_owner, Owned(accountId, "PRAC-50K"));
        Reconciles(accountId, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MES.U26", 2)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        JsonElement account = Parse(result).GetProperty("accounts")[0];
        account.GetProperty("account").GetString().Should().Be("PRAC-50K");
        account.GetProperty("markBasis").GetString().Should().Be("Live");
        JsonElement position = account.GetProperty("positions")[0];
        position.GetProperty("contract").GetString().Should().Be("CON.F.US.MES.U26");
        position.GetProperty("netQuantity").GetInt32().Should().Be(2);
        position.GetProperty("side").GetString().Should().Be("Long");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportOnlyTheOwnersAccounts()
    {
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        await SeedAsync(_owner, Owned(mine, "MINE"));
        await SeedAsync(Guid.NewGuid(), Owned(theirs, "THEIRS"));
        Reconciles(mine, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MES.U26", 1)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        result.Should().Contain("MINE").And.NotContain("THEIRS");
        // The other operator's account is filtered out before the venue is ever consulted for it (R-20).
        A.CallTo(() => _reconciler.ReconcileAsync(theirs, A<DateTimeOffset>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDeclareUnknown_WhenVenueTruthIsUnobtainable_NotAFabricatedFlat()
    {
        Guid accountId = Guid.NewGuid();
        await SeedAsync(_owner, Owned(accountId, "PRAC-50K"));
        // The reconciler's declared-unknown contract: an unreachable venue is Unknown with NO positions.
        Reconciles(accountId, new PositionReconciliation(PositionMarkBasis.Unknown, []));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        JsonElement account = Parse(result).GetProperty("accounts")[0];
        // The operator is told the truth is unknown -- never shown an empty book that reads as "flat".
        account.GetProperty("markBasis").GetString().Should().Be("Unknown");
        account.GetProperty("positions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldOmitFlatContracts_ButStillReportTheAccountAndItsLiveBasis()
    {
        Guid accountId = Guid.NewGuid();
        await SeedAsync(_owner, Owned(accountId, "PRAC-50K"));
        // A flat contract (net 0) is not an open position -- omit it -- but the account is reachable and Live, which
        // is a distinct state from Unknown (both have zero positions; only the basis tells them apart).
        Reconciles(accountId, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MES.U26", 0)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        JsonElement account = Parse(result).GetProperty("accounts")[0];
        account.GetProperty("markBasis").GetString().Should().Be("Live");
        account.GetProperty("positions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportAShortWithItsSignedQuantity()
    {
        Guid accountId = Guid.NewGuid();
        await SeedAsync(_owner, Owned(accountId, "PRAC-50K"));
        Reconciles(accountId, new PositionReconciliation(PositionMarkBasis.Settlement, [Position("CON.F.US.MNQ.U26", -3)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        JsonElement position = Parse(result).GetProperty("accounts")[0].GetProperty("positions")[0];
        position.GetProperty("netQuantity").GetInt32().Should().Be(-3);
        position.GetProperty("side").GetString().Should().Be("Short");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipInactiveAccounts()
    {
        Guid active = Guid.NewGuid();
        Guid inactive = Guid.NewGuid();
        await SeedAsync(_owner, Owned(active, "ACTIVE"), Owned(inactive, "DEACTIVATED", active: false));
        Reconciles(active, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MES.U26", 1)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        result.Should().Contain("ACTIVE").And.NotContain("DEACTIVATED");
        // A deactivated login yields no venue truth, so it is never reconciled.
        A.CallTo(() => _reconciler.ReconcileAsync(inactive, A<DateTimeOffset>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmptyAccounts_WhenTheOperatorHasNone()
    {
        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        Parse(result).GetProperty("accounts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenTheReconcileFaults()
    {
        Guid accountId = Guid.NewGuid();
        await SeedAsync(_owner, Owned(accountId, "PRAC-50K"));
        A.CallTo(() => _reconciler.ReconcileAsync(accountId, A<DateTimeOffset>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue transport blew up"));

        // A read fault returns a compact error result -- never a throw, and never a partial / invented book.
        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportEachOfSeveralAccounts()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        await SeedAsync(_owner, Owned(first, "PRAC-50K"), Owned(second, "EVAL-100K"));
        Reconciles(first, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MES.U26", 1)]));
        Reconciles(second, new PositionReconciliation(PositionMarkBasis.Live, [Position("CON.F.US.MNQ.U26", 2)]));

        string result = await Tool().ExecuteAsync("{}", CancellationToken.None);

        Parse(result).GetProperty("accounts").GetArrayLength().Should().Be(2);
        result.Should().Contain("PRAC-50K").And.Contain("EVAL-100K");
    }
}
