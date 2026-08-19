using FakeItEasy;
using MarqSpec.Client.Tradovate;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The read-only Tradovate venue (gh#977 slice 1). It reads accounts (with host-derived mode and joined balances) and
/// positions, resolves contracts, and refuses market-data and execution loudly through the capability seam until
/// those slices land — so nothing partial or silent reaches a caller.
/// </summary>
public class TradovateVenueTests
{
    private static VenueId Tradovate { get; } = VenueId.Parse("tradovate");
    private readonly ITradovateApiClient _api = A.Fake<ITradovateApiClient>();

    private TradovateVenue CreateVenue(string host = "https://demo.tradovateapi.com/v1", FirmConventions? conventions = null)
    {
        A.CallTo(() => _api.ConfiguredHost).Returns(host);
        return new TradovateVenue(_api, conventions ?? FirmConventions.ForBrokerage("Tradovate"));
    }

    [Fact]
    public void Id_ShouldBeTradovate()
    {
        CreateVenue().Id.Should().Be(Tradovate);
    }

    [Fact]
    public void Capabilities_ShouldGrantNothing_InTheReadOnlySlice()
    {
        CreateVenue().Capabilities.Should().Be(VenueCapabilities.None);
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldJoinBalancesAndResolvePracticeOnTheDemoHost()
    {
        IReadOnlyList<ClientModels.Account> accounts = [Account(9001), Account(9002)];
        IReadOnlyList<ClientModels.CashBalance> balances = [CashBalance(9001, 7500m)]; // 9002 has no cash-balance row
        A.CallTo(() => _api.GetAccountsAsync(A<CancellationToken>._)).Returns(accounts);
        A.CallTo(() => _api.GetCashBalancesAsync(A<CancellationToken>._)).Returns(balances);

        IReadOnlyList<VenueAccount> result = await CreateVenue(host: "https://demo.tradovateapi.com/v1").GetAccountsAsync();

        result.Should().HaveCount(2);

        VenueAccount first = result.Single(account => account.Id == VenueAccountId.Create(Tradovate, "9001"));
        first.Balance.Should().Be(7500m);
        first.Mode.Should().Be(TradingMode.Practice);

        VenueAccount second = result.Single(account => account.Id == VenueAccountId.Create(Tradovate, "9002"));
        second.Balance.Should().Be(0m); // no cash-balance row -> zero, not a failed discovery
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldResolveLive_OnTheLiveHost()
    {
        IReadOnlyList<ClientModels.Account> accounts = [Account(9001)];
        IReadOnlyList<ClientModels.CashBalance> balances = [];
        A.CallTo(() => _api.GetAccountsAsync(A<CancellationToken>._)).Returns(accounts);
        A.CallTo(() => _api.GetCashBalancesAsync(A<CancellationToken>._)).Returns(balances);

        IReadOnlyList<VenueAccount> result = await CreateVenue(host: "https://live.tradovateapi.com/v1").GetAccountsAsync();

        result.Single().Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public async Task GetPositionsAsync_ShouldFilterToTheAccountAndSkipFlat()
    {
        IReadOnlyList<ClientModels.Position> positions =
        [
            Position(accountId: 9001, contractId: 7, netPos: 2), // this account, open -> kept
            Position(accountId: 9001, contractId: 8, netPos: 0), // this account, flat -> skipped
            Position(accountId: 9002, contractId: 9, netPos: 5), // another account -> skipped
        ];
        A.CallTo(() => _api.GetPositionsAsync(A<CancellationToken>._)).Returns(positions);

        IReadOnlyList<PositionSnapshot> result =
            await CreateVenue().GetPositionsAsync(VenueAccountId.Create(Tradovate, "9001"));

        result.Should().ContainSingle();
        result.Single().Contract.Should().Be(VenueContractId.Create(Tradovate, "7"));
        result.Single().NetQuantity.Should().Be(2);
    }

    [Fact]
    public async Task GetPositionsAsync_ShouldThrow_ForAForeignAccount()
    {
        Func<Task> act = () =>
            CreateVenue().GetPositionsAsync(VenueAccountId.Create(VenueId.Parse("projectx"), "9001"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldMapAFoundContract()
    {
        A.CallTo(() => _api.FindContractAsync("ESM24", A<CancellationToken>._))
            .Returns(new ClientModels.Contract { Id = 123, Name = "ESM24", ContractMaturityId = 1 });

        ResolvedContract resolved = await CreateVenue().ResolveContractAsync(InstrumentId.Parse("ESM24"));

        resolved.Contract.Should().Be(VenueContractId.Create(Tradovate, "123"));
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldThrow_WhenNoContractMatches()
    {
        A.CallTo(() => _api.FindContractAsync(A<string>._, A<CancellationToken>._)).Returns((ClientModels.Contract?)null);

        Func<Task> act = () => CreateVenue().ResolveContractAsync(InstrumentId.Parse("ZZ"));

        await act.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task GetBarsAsync_ShouldRefuse_WhileBarsAreUngranted()
    {
        Func<Task> act = () => CreateVenue().GetBarsAsync(
            VenueContractId.Create(Tradovate, "7"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
    }

    [Fact]
    public void StreamQuotesAsync_ShouldRefuse_WhileQuotesAreUngranted()
    {
        // Eager refusal: the capability is checked when the sequence is requested, not on the first read.
        Action act = () => CreateVenue().StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"));

        act.Should().Throw<VenueCapabilityNotSupportedException>();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldRefuse_WhileClosePositionIsUngranted()
    {
        Func<Task> act = () => CreateVenue().ClosePositionAsync(
            VenueAccountId.Create(Tradovate, "9001"), VenueContractId.Create(Tradovate, "7"));

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldRefuse_InTheReadOnlySlice()
    {
        Func<Task> act = () => CreateVenue().PlaceOrderAsync(null!);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldRefuse_InTheReadOnlySlice()
    {
        Func<Task> act = () => CreateVenue().CancelOrderAsync(VenueAccountId.Create(Tradovate, "9001"), "1");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static ClientModels.Account Account(long? id, bool active = true, bool isReadonly = false) =>
        new()
        {
            Id = id,
            Name = "Acct",
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

    private static ClientModels.CashBalance CashBalance(long accountId, decimal amount) =>
        new()
        {
            AccountId = accountId,
            Timestamp = DateTimeOffset.UnixEpoch,
            TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 8, Day = 18 },
            CurrencyId = 1,
            Amount = amount,
        };

    private static ClientModels.Position Position(long accountId, long contractId, int netPos) =>
        new()
        {
            AccountId = accountId,
            ContractId = contractId,
            Timestamp = DateTimeOffset.UnixEpoch,
            TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 8, Day = 18 },
            NetPos = netPos,
            NetPrice = 5000m,
            Bought = 0,
            BoughtValue = 0m,
            Sold = 0,
            SoldValue = 0m,
            PrevPos = 0,
        };
}
