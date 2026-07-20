using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// Where ProjectX's vocabulary becomes ours. Every case here is a real difference between the gateway's shape and
/// the venue-neutral model — the mappings that would silently corrupt risk or execution if they were wrong.
/// </summary>
public class ProjectXMappingTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    // --- Practice vs. live: the R-14 input ---

    [Fact]
    public void ToVenueAccount_ShouldBePractice_WhenTheAccountIsSimulated()
    {
        ClientModels.TradingAccount account = new() { Id = 9001, Name = "PRAC-50K-1234", Simulated = true };

        ProjectXMapping.ToVenueAccount(account, Venue).Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public void ToVenueAccount_ShouldBeLive_WhenTheAccountIsNotSimulated()
    {
        // Fail-safe direction: anything the venue does not mark simulated is treated as real money, which
        // TradingModePolicy then refuses outside production.
        ClientModels.TradingAccount account = new() { Id = 9002, Name = "50KTC-V2-DLL-0000", Simulated = false };

        ProjectXMapping.ToVenueAccount(account, Venue).Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ToVenueAccount_ShouldIgnoreAPracticeLookingName_WhenTheVenueSaysNotSimulated()
    {
        // The name is not authoritative. Honouring "PRAC-" here could only reclassify a live account as
        // practice -- the one direction that risks real money.
        ClientModels.TradingAccount account = new() { Id = 9003, Name = "PRAC-LOOKING-NAME", Simulated = false };

        ProjectXMapping.ToVenueAccount(account, Venue).Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ToVenueAccount_ShouldTagTheAccountWithTheVenueAndItsId()
    {
        ClientModels.TradingAccount account = new()
        {
            Id = 9001,
            Name = "PRAC-50K-1234",
            Balance = 50_000m,
            CanTrade = true,
            IsVisible = true,
            Simulated = true,
        };

        VenueAccount mapped = ProjectXMapping.ToVenueAccount(account, Venue);

        mapped.Id.Should().Be(VenueAccountId.Create(Venue, "9001"));
        mapped.Name.Should().Be("PRAC-50K-1234");
        mapped.Balance.Should().Be(50_000m);
        mapped.IsSelectable.Should().BeTrue();
    }

    [Fact]
    public void ToVenueAccount_ShouldNotBeSelectable_WhenTheVenueSaysItCannotTrade()
    {
        ClientModels.TradingAccount account = new() { Id = 9004, Name = "PRAC", CanTrade = false, IsVisible = true };

        ProjectXMapping.ToVenueAccount(account, Venue).IsSelectable.Should().BeFalse();
    }

    // --- Contract money math ---

    [Fact]
    public void ToInstrumentSpec_ShouldDerivePointValueFromTickValueAndTickSize()
    {
        // ProjectX publishes value-per-tick, not value-per-point. ES: $12.50 a tick at 0.25 => $50 a point.
        ClientModels.Contract contract = new() { Id = "CON.F.US.EP.M25", TickSize = 0.25m, TickValue = 12.50m };

        InstrumentSpec spec = ProjectXMapping.ToInstrumentSpec(contract, InstrumentId.Parse("ES"));

        spec.TickSize.Should().Be(0.25m);
        spec.PointValue.Should().Be(50m);
    }

    [Fact]
    public void ToInstrumentSpec_ShouldThrow_WhenTheContractHasNoTickSize()
    {
        ClientModels.Contract contract = new() { Id = "CON.BAD", TickSize = 0m, TickValue = 12.50m };

        Action act = () => ProjectXMapping.ToInstrumentSpec(contract, InstrumentId.Parse("ES"));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- Positions: unsigned size + a direction enum become a signed quantity ---

    [Fact]
    public void ToPositionSnapshot_ShouldSignQuantityPositive_WhenThePositionIsLong()
    {
        ClientModels.Position position = new()
        {
            AccountId = 9001,
            ContractId = "CON.F.US.EP.M25",
            Type = ClientModels.PositionType.Long,
            Size = 3,
            AveragePrice = 5_000.25m,
        };

        PositionSnapshot mapped = ProjectXMapping.ToPositionSnapshot(position, Venue);

        mapped.NetQuantity.Should().Be(3);
        mapped.IsLong.Should().BeTrue();
        mapped.Account.Should().Be(VenueAccountId.Create(Venue, "9001"));
    }

    [Fact]
    public void ToPositionSnapshot_ShouldSignQuantityNegative_WhenThePositionIsShort()
    {
        // ProjectX reports size unsigned with a separate direction -- dropping the sign would invert risk.
        ClientModels.Position position = new()
        {
            AccountId = 9001,
            ContractId = "CON.F.US.EP.M25",
            Type = ClientModels.PositionType.Short,
            Size = 3,
            AveragePrice = 5_000.25m,
        };

        PositionSnapshot mapped = ProjectXMapping.ToPositionSnapshot(position, Venue);

        mapped.NetQuantity.Should().Be(-3);
        mapped.IsShort.Should().BeTrue();
    }

    [Fact]
    public void ToPositionSnapshot_ShouldBeFlat_WhenTheDirectionIsUndefined()
    {
        ClientModels.Position position = new()
        {
            AccountId = 9001,
            ContractId = "CON.F.US.EP.M25",
            Type = ClientModels.PositionType.Undefined,
            Size = 0,
        };

        ProjectXMapping.ToPositionSnapshot(position, Venue).IsFlat.Should().BeTrue();
    }

    [Fact]
    public void ToPositionSnapshot_ShouldThrow_WhenTheDirectionIsUndefinedButSizeIsNot()
    {
        // Calling this flat would tell the risk gate and auto-flatten there is nothing open while real exposure
        // sits on the account. Refuse to map it rather than manufacture a zero.
        ClientModels.Position position = new()
        {
            AccountId = 9001,
            ContractId = "CON.F.US.EP.M25",
            Type = ClientModels.PositionType.Undefined,
            Size = 2,
        };

        Action act = () => ProjectXMapping.ToPositionSnapshot(position, Venue);

        act.Should().Throw<ProjectXVenueException>();
    }

    // --- Account keys cross the seam as strings, but ProjectX wants an int ---

    [Fact]
    public void ToAccountId_ShouldParseTheVenueAccountKey()
    {
        ProjectXMapping.ToAccountId(VenueAccountId.Create(Venue, "9001"), Venue).Should().Be(9001);
    }

    [Fact]
    public void ToAccountId_ShouldThrow_WhenTheKeyIsNotAProjectXAccountNumber()
    {
        Action act = () => ProjectXMapping.ToAccountId(VenueAccountId.Create(Venue, "not-a-number"), Venue);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToAccountId_ShouldThrow_WhenTheAccountBelongsToAnotherVenue()
    {
        // Account handles are bare integers that collide across venues -- tradovate:9001 must never be sent to
        // ProjectX account 9001, which is a different and possibly real-money account.
        VenueAccountId foreign = VenueAccountId.Create(VenueId.Parse("tradovate"), "9001");

        Action act = () => ProjectXMapping.ToAccountId(foreign, Venue);

        act.Should().Throw<ArgumentException>().WithMessage("*tradovate*");
    }

    [Fact]
    public void ToContractKey_ShouldUnwrapTheHandle_WhenTheContractBelongsToThisVenue()
    {
        ProjectXMapping.ToContractKey(VenueContractId.Create(Venue, "CON.F.US.EP.M25"), Venue)
            .Should().Be("CON.F.US.EP.M25");
    }

    [Fact]
    public void ToContractKey_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        VenueContractId foreign = VenueContractId.Create(VenueId.Parse("tradovate"), "ESM25");

        Action act = () => ProjectXMapping.ToContractKey(foreign, Venue);

        act.Should().Throw<ArgumentException>().WithMessage("*tradovate*");
    }

    // --- Orders ---

    [Theory]
    [InlineData(OrderSide.Buy, ClientModels.OrderSide.Bid)]
    [InlineData(OrderSide.Sell, ClientModels.OrderSide.Ask)]
    public void ToClientSide_ShouldMapDirection(OrderSide side, ClientModels.OrderSide expected)
    {
        ProjectXMapping.ToClientSide(side).Should().Be(expected);
    }

    [Theory]
    [InlineData(OrderType.Market, ClientModels.OrderType.Market)]
    [InlineData(OrderType.Limit, ClientModels.OrderType.Limit)]
    [InlineData(OrderType.Stop, ClientModels.OrderType.Stop)]
    [InlineData(OrderType.StopLimit, ClientModels.OrderType.StopLimit)]
    [InlineData(OrderType.TrailingStop, ClientModels.OrderType.TrailingStop)]
    public void ToClientType_ShouldMapEveryVenueNeutralOrderType(OrderType type, ClientModels.OrderType expected)
    {
        ProjectXMapping.ToClientType(type).Should().Be(expected);
    }

    // --- Bars and quotes ---

    [Fact]
    public void ToBar_ShouldMapOhlcvAndTreatTheTimestampAsUtc()
    {
        ClientModels.AggregateBar bar = new()
        {
            Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Unspecified),
            Open = 5_000m,
            High = 5_010m,
            Low = 4_995m,
            Close = 5_005m,
            Volume = 1_200,
        };

        Bar mapped = ProjectXMapping.ToBar(bar);

        mapped.OpenTime.Should().Be(new DateTimeOffset(2026, 7, 20, 14, 30, 0, TimeSpan.Zero));
        mapped.Close.Should().Be(new Price(5_005m));
        mapped.Volume.Should().Be(1_200);
    }

    [Fact]
    public void ToQuote_ShouldLeaveRestingSizeAbsent_WhenTheStreamCarriesPricesOnly()
    {
        // ProjectX's quote stream publishes prices; resting size lives on the depth stream. Absent, not zero.
        ClientModels.PriceUpdate update = new()
        {
            BestBid = 5_000m,
            BestAsk = 5_000.25m,
            Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc),
        };

        Quote mapped = ProjectXMapping.ToQuote(update);

        mapped.Bid.Should().Be(new Price(5_000m));
        mapped.Ask.Should().Be(new Price(5_000.25m));
        mapped.BidSize.Should().BeNull();
        mapped.AskSize.Should().BeNull();
    }

    // --- Bar size becomes ProjectX's unit + count ---

    [Theory]
    [InlineData(30, ClientModels.AggregateBarUnit.Second, 30)]
    [InlineData(60, ClientModels.AggregateBarUnit.Minute, 1)]
    [InlineData(300, ClientModels.AggregateBarUnit.Minute, 5)]
    [InlineData(3600, ClientModels.AggregateBarUnit.Hour, 1)]
    [InlineData(86400, ClientModels.AggregateBarUnit.Day, 1)]
    public void ToBarUnit_ShouldChooseTheCoarsestExactUnit(int seconds, ClientModels.AggregateBarUnit unit, int number)
    {
        (ClientModels.AggregateBarUnit Unit, int Number) mapped =
            ProjectXMapping.ToBarUnit(TimeSpan.FromSeconds(seconds));

        mapped.Unit.Should().Be(unit);
        mapped.Number.Should().Be(number);
    }

    [Fact]
    public void ToBarUnit_ShouldThrow_WhenTheBarSizeIsNotAWholeNumberOfSeconds()
    {
        Action act = () => ProjectXMapping.ToBarUnit(TimeSpan.FromMilliseconds(1500));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
