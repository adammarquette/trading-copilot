using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The Tradovate mapping (gh#977). The cases that matter are the safety-relevant ones: the demo/live host is the
/// mode source for a brokerage (gh#780) and an unrecognised host must fail closed to <see cref="TradingMode.Undeclared"/>;
/// <c>netPos</c> is already signed and must not be inverted; and the venue-qualifier guard must never let a foreign
/// account handle reach Tradovate.
/// </summary>
public class TradovateMappingTests
{
    private static VenueId Tradovate { get; } = VenueId.Parse("tradovate");

    [Theory]
    [InlineData("https://demo.tradovateapi.com/v1")]
    [InlineData("https://DEMO.tradovateapi.com/v1")]
    public void IsSimulatedHost_ShouldBeTrue_ForTheDemoHost(string host)
    {
        TradovateMapping.IsSimulatedHost(host).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://live.tradovateapi.com/v1")]
    [InlineData("https://LIVE.tradovateapi.com/v1")]
    public void IsSimulatedHost_ShouldBeFalse_ForTheLiveHost(string host)
    {
        TradovateMapping.IsSimulatedHost(host).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://api.tradovate.com")]
    [InlineData("https://example.com")]
    public void IsSimulatedHost_ShouldBeNull_ForAnUnrecognisedHost_SoItFailsClosed(string host)
    {
        // Null resolves to Undeclared (tradeable nowhere). An unexpected host must never default an account to a real mode.
        TradovateMapping.IsSimulatedHost(host).Should().BeNull();
    }

    [Fact]
    public void ToVenueAccount_ShouldResolvePractice_OnADemoHost_ForABrokerage()
    {
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(9001), balance: 5000m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: true);

        account.Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public void ToVenueAccount_ShouldResolveLive_OnALiveHost_ForABrokerage()
    {
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(9001), balance: 5000m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: false);

        account.Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public void IsSimulatedHost_ShouldClassifyByHostComponent_NotASubstringOfTheWholeUrl()
    {
        // A live host with a demo-looking query must NOT read as Practice: the classification is on the URL's host
        // component, never a substring of the whole string.
        TradovateMapping.IsSimulatedHost("https://live.tradovateapi.com/v1?ref=demo.tradovateapi.com").Should().BeFalse();
    }

    [Fact]
    public void IsSimulatedHost_ShouldBeNull_ForAnUnparseableValue()
    {
        TradovateMapping.IsSimulatedHost("not a url").Should().BeNull();
    }

    [Fact]
    public void ToVenueAccount_ShouldResolveUndeclared_ForNonBrokerageConventions_SoModeNeverDefaultsOpen()
    {
        // Only ForBrokerage reads the venue flag. None (or prop conventions) leaves a Tradovate account Undeclared --
        // the fail-closed direction if the factory ever supplied the wrong conventions.
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(9001), balance: 0m, Tradovate, FirmConventions.None, venueReportsSimulated: true);

        account.Mode.Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void ToVenueAccount_ShouldMapIdNameBalanceStageAndRawSimulatedFlag()
    {
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(42, name: "MyAcct"), balance: 1234.5m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: true);

        account.Id.Should().Be(VenueAccountId.Create(Tradovate, "42"));
        account.Name.Should().Be("MyAcct");
        account.Balance.Should().Be(1234.5m);
        account.Stage.Should().Be(AccountStage.Unknown);
        account.VenueReportsSimulated.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, true)]    // active, explicitly not read-only -> tradeable
    [InlineData(true, true, false)]    // active but read-only -> not tradeable
    [InlineData(false, false, false)]  // inactive -> not tradeable
    public void ToVenueAccount_ShouldSetCanTrade_FromActiveAndReadonly(bool active, bool isReadonly, bool expected)
    {
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(9001, active: active, isReadonly: isReadonly), balance: 0m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: true);

        account.CanTrade.Should().Be(expected);
    }

    [Fact]
    public void ToVenueAccount_ShouldNotBeTradable_WhenReadonlyIsUnknown()
    {
        // Fail-closed (review #990): an unknown (null) Readonly must not be assumed tradable — CanTrade is the gate
        // OrderExecutionService checks, so an unclassifiable read-only status defaults to not-tradable.
        VenueAccount account = TradovateMapping.ToVenueAccount(
            Account(9001, active: true, isReadonly: null), balance: 0m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: true);

        account.CanTrade.Should().BeFalse();
    }

    [Fact]
    public void ToVenueAccount_ShouldThrow_WhenTheAccountHasNoId()
    {
        Action act = () => TradovateMapping.ToVenueAccount(
            Account(null), balance: 0m, Tradovate, FirmConventions.ForBrokerage("t"), venueReportsSimulated: true);

        act.Should().Throw<TradovateVenueException>();
    }

    [Fact]
    public void ToPositionSnapshot_ShouldKeepALongPositionPositive()
    {
        PositionSnapshot snapshot = TradovateMapping.ToPositionSnapshot(
            Position(accountId: 9001, contractId: 7, netPos: 3, netPrice: 5000m), Tradovate);

        snapshot.NetQuantity.Should().Be(3);
        snapshot.Account.Should().Be(VenueAccountId.Create(Tradovate, "9001"));
        snapshot.Contract.Should().Be(VenueContractId.Create(Tradovate, "7"));
        snapshot.AveragePrice.Should().Be(new Price(5000m));
    }

    [Fact]
    public void ToPositionSnapshot_ShouldKeepAShortPositionNegative()
    {
        // netPos is already signed; a short arrives negative and must stay negative -- inverting it would flip every
        // downstream risk and flatten decision.
        PositionSnapshot snapshot = TradovateMapping.ToPositionSnapshot(
            Position(accountId: 9001, contractId: 7, netPos: -2, netPrice: 4990m), Tradovate);

        snapshot.NetQuantity.Should().Be(-2);
    }

    [Fact]
    public void ToPositionSnapshot_ShouldTreatAnAbsentNetPriceAsZero_ForAFlatPosition()
    {
        // A flat position's price is immaterial; only a HELD one is required to carry a price (below).
        PositionSnapshot snapshot = TradovateMapping.ToPositionSnapshot(
            Position(accountId: 9001, contractId: 7, netPos: 0, netPrice: null), Tradovate);

        snapshot.AveragePrice.Should().Be(new Price(0m));
    }

    [Fact]
    public void ToPositionSnapshot_ShouldThrow_ForAHeldPositionWithNoNetPrice()
    {
        // Absent != zero: an OPEN position (netPos != 0) with a null netPrice must not fabricate a 0 average entry —
        // that would feed a wrong unrealised-P&L basis to any risk / P&L consumer, so refuse it loudly.
        Action act = () => TradovateMapping.ToPositionSnapshot(
            Position(accountId: 9001, contractId: 7, netPos: 2, netPrice: null), Tradovate);

        act.Should().Throw<TradovateVenueException>();
    }

    [Fact]
    public void ToResolvedContract_ShouldPairTheHandleWithTheInstrument()
    {
        ResolvedContract resolved = TradovateMapping.ToResolvedContract(
            new ClientModels.Contract { Id = 123, Name = "ESM24", ContractMaturityId = 1 }, InstrumentId.Parse("ES"), Tradovate);

        resolved.Contract.Should().Be(VenueContractId.Create(Tradovate, "123"));
        resolved.Instrument.Should().Be(InstrumentId.Parse("ES"));
    }

    [Fact]
    public void ToResolvedContract_ShouldThrow_WhenTheContractHasNoId()
    {
        Action act = () => TradovateMapping.ToResolvedContract(
            new ClientModels.Contract { Id = null, Name = "ESM24", ContractMaturityId = 1 }, InstrumentId.Parse("ES"), Tradovate);

        act.Should().Throw<TradovateVenueException>();
    }

    [Fact]
    public void ToAccountId_ShouldParseAnOwnedAccount()
    {
        TradovateMapping.ToAccountId(VenueAccountId.Create(Tradovate, "9001"), Tradovate).Should().Be(9001L);
    }

    [Fact]
    public void ToAccountId_ShouldThrow_ForAForeignVenue()
    {
        // projectx:9001 must never be sent to Tradovate account 9001 (a different, possibly real-money account).
        Action act = () => TradovateMapping.ToAccountId(VenueAccountId.Create(VenueId.Parse("projectx"), "9001"), Tradovate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToAccountId_ShouldThrow_ForANonNumericKey()
    {
        Action act = () => TradovateMapping.ToAccountId(VenueAccountId.Create(Tradovate, "abc"), Tradovate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToChartUnit_ShouldMapWholeMinutesToMinuteBars()
    {
        TradovateMapping.ToChartUnit(TimeSpan.FromMinutes(5)).Should().Be((ClientModels.ChartUnderlyingType.MinuteBar, 5));
    }

    [Fact]
    public void ToChartUnit_ShouldMapAnHourTo60MinuteElements_NotAnHourBar()
    {
        // Tradovate has no hour bar; an hour is 60 minute-elements.
        TradovateMapping.ToChartUnit(TimeSpan.FromHours(1)).Should().Be((ClientModels.ChartUnderlyingType.MinuteBar, 60));
    }

    [Fact]
    public void ToChartUnit_ShouldMapWholeDaysToDailyBars()
    {
        TradovateMapping.ToChartUnit(TimeSpan.FromDays(1)).Should().Be((ClientModels.ChartUnderlyingType.DailyBar, 1));
    }

    [Theory]
    [InlineData(30)] // 30s — sub-minute
    [InlineData(90)] // 90s — not a whole minute
    public void ToChartUnit_ShouldThrow_ForASubMinuteOrFractionalBar(int seconds)
    {
        Action act = () => TradovateMapping.ToChartUnit(TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToChartUnit_ShouldMapMultipleWholeDaysToDailyBars()
    {
        TradovateMapping.ToChartUnit(TimeSpan.FromDays(2)).Should().Be((ClientModels.ChartUnderlyingType.DailyBar, 2));
    }

    [Fact]
    public void ToChartUnit_ShouldThrow_ForAZeroBar()
    {
        Action act = () => TradovateMapping.ToChartUnit(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToChartUnit_ShouldThrow_ForANegativeBar()
    {
        Action act = () => TradovateMapping.ToChartUnit(TimeSpan.FromMinutes(-5));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToChartRequest_ShouldCarryTheContractIdRangeAndUnit()
    {
        DateTimeOffset from = DateTimeOffset.UnixEpoch;
        DateTimeOffset to = from.AddHours(1);

        ClientModels.ChartRequest request = TradovateMapping.ToChartRequest(
            VenueContractId.Create(Tradovate, "123"), from, to, TimeSpan.FromMinutes(5), Tradovate);

        request.ContractId.Should().Be(123L);
        request.UnderlyingType.Should().Be(ClientModels.ChartUnderlyingType.MinuteBar);
        request.ElementSize.Should().Be(5);
        request.AsFarAsTimestamp.Should().Be(from); // oldest edge
        request.ClosestTimestamp.Should().Be(to);   // newest edge
        request.AsMuchAsElements.Should().Be(1000); // an explicit cap, not left unbounded
    }

    [Fact]
    public void ToChartRequest_ShouldThrow_ForAnInvertedRange()
    {
        // from after to is a nonsense window; a swapped-edge request would come back empty and read as "no bars".
        Action act = () => TradovateMapping.ToChartRequest(
            VenueContractId.Create(Tradovate, "123"),
            DateTimeOffset.UnixEpoch.AddHours(1), DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1), Tradovate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToChartRequest_ShouldThrow_ForAForeignVenueContract()
    {
        Action act = () => TradovateMapping.ToChartRequest(
            VenueContractId.Create(VenueId.Parse("projectx"), "123"),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), TimeSpan.FromMinutes(1), Tradovate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToChartRequest_ShouldThrow_ForANonNumericContractKey()
    {
        Action act = () => TradovateMapping.ToChartRequest(
            VenueContractId.Create(Tradovate, "ES"),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), TimeSpan.FromMinutes(1), Tradovate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToBar_ShouldMapOhlcvWithTheTimestamp()
    {
        Bar bar = TradovateMapping.ToBar(new ClientModels.ChartBar
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Open = 5000m,
            High = 5010m,
            Low = 4990m,
            Close = 5005m,
            Volume = 1234m,
        });

        bar.OpenTime.Should().Be(DateTimeOffset.UnixEpoch);
        bar.Open.Should().Be(new Price(5000m));
        bar.High.Should().Be(new Price(5010m));
        bar.Low.Should().Be(new Price(4990m));
        bar.Close.Should().Be(new Price(5005m));
        bar.Volume.Should().Be(1234L);
    }

    [Fact]
    public void ToBar_ShouldTreatAnAbsentVolumeAsZero()
    {
        // The domain bar has an integer volume with no "absent"; an omitted volume maps to 0.
        Bar bar = TradovateMapping.ToBar(new ClientModels.ChartBar
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Open = 1m,
            High = 1m,
            Low = 1m,
            Close = 1m,
            Volume = null,
        });

        bar.Volume.Should().Be(0L);
    }

    private static ClientModels.Account Account(long? id, string name = "Acct", bool active = true, bool? isReadonly = false) =>
        new()
        {
            Id = id,
            Name = name,
            UserId = 1,
            AccountType = default,
            Active = active,
            ClearingHouseId = 1,
            RiskCategoryId = 1,
            AutoLiqProfileId = 1,
            MarginAccountType = default,
            LegalStatus = default,
            Readonly = isReadonly,
        };

    private static ClientModels.Position Position(long accountId, long contractId, int netPos, decimal? netPrice) =>
        new()
        {
            AccountId = accountId,
            ContractId = contractId,
            Timestamp = DateTimeOffset.UnixEpoch,
            TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 8, Day = 18 },
            NetPos = netPos,
            NetPrice = netPrice,
            Bought = 0,
            BoughtValue = 0m,
            Sold = 0,
            SoldValue = 0m,
            PrevPos = 0,
        };
}
