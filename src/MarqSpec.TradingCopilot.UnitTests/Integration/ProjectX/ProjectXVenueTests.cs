using FakeItEasy;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// The adapter behind <see cref="ITradingVenue"/>. The cases that matter most are the ones where ProjectX reports
/// failure without throwing — treating those as success would mean believing an order was placed when it was not.
/// </summary>
public class ProjectXVenueTests
{
    private const string ContractKey = "CON.F.US.EP.M25";

    private readonly IProjectXApiClient _api = A.Fake<IProjectXApiClient>();
    private readonly IProjectXWebSocketClient _webSocket = A.Fake<IProjectXWebSocketClient>();
    private readonly ProjectXVenue _venue;

    public ProjectXVenueTests()
    {
        _venue = new ProjectXVenue(_api, _webSocket, ProjectXDataTier.Simulated, FirmConventions.None);
    }

    private VenueAccountId Account => VenueAccountId.Create(_venue.Id, "9001");

    private VenueContractId Contract => VenueContractId.Create(_venue.Id, ContractKey);

    private OrderRequest MarketBuy(int quantity = 1)
    {
        return new OrderRequest(
            Account,
            Contract,
            OrderSide.Buy,
            OrderType.Market,
            quantity,
            LimitPrice: null,
            StopPrice: null);
    }

    // --- Identity and capabilities ---

    [Fact]
    public void Id_ShouldBeProjectX()
    {
        _venue.Id.Should().Be(VenueId.Parse("projectx"));
    }

    [Fact]
    public void Capabilities_ShouldDeclareWhatTheAdapterCanActuallyDeliver()
    {
        _venue.Capabilities.Supports(VenueCapability.HistoricalBars).Should().BeTrue();
        _venue.Capabilities.Supports(VenueCapability.Quotes).Should().BeTrue();
        _venue.Capabilities.Supports(VenueCapability.ClosePosition).Should().BeTrue();
        _venue.Capabilities.Supports(VenueCapability.BracketOrders).Should().BeTrue(); // the always-native safety stop (gh#11 inc 3)
        _venue.Capabilities.Supports(VenueCapability.AccountStreaming).Should().BeTrue(); // the user-hub seam (gh#219)
        _venue.Capabilities.Supports(VenueCapability.ModifyOrder).Should().BeTrue(); // in-place reprice (gh#259)
    }

    [Theory]
    [InlineData(VenueCapability.TrailingStops)]
    [InlineData(VenueCapability.MarketDepth)]
    public void Capabilities_ShouldNotAdvertiseWhatTheNeutralContractCannotReach(VenueCapability capability)
    {
        // ProjectX does all of these; ITradingVenue exposes none of them yet. Advertising them would send a
        // caller down a path it cannot take -- the opposite of the graceful degradation the model exists for.
        _venue.Capabilities.Supports(capability).Should().BeFalse();
    }

    // --- The data tier: asking the wrong universe returns nothing, not an error ---

    [Fact]
    public void Constructor_ShouldThrow_WhenTheDataTierIsNotRecognized()
    {
        // Falling through to simulated would silently recreate the empty-universe failure the
        // required parameter exists to prevent.
        Action act = () => new ProjectXVenue(_api, _webSocket, (ProjectXDataTier)99, FirmConventions.None);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldQueryTheSimulatedUniverse_WhenTheTierIsSimulated()
    {
        // Practice credentials against live:true return an EMPTY contract list rather than an error, which
        // surfaces later as "no contract matches ES". Verified against a live practice account.
        A.CallTo(() => _api.SearchContractsAsync("ES", false, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>(
                [new ClientModels.Contract { Id = ContractKey, ActiveContract = true }]);

        ResolvedContract resolved = await _venue.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        resolved.Contract.Key.Should().Be(ContractKey);
        resolved.Instrument.Should().Be(InstrumentId.Parse("ES"));
        A.CallTo(() => _api.SearchContractsAsync("ES", true, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldQueryTheLiveUniverse_WhenTheTierIsLive()
    {
        ProjectXVenue live = new(_api, _webSocket, ProjectXDataTier.Live, FirmConventions.None);
        A.CallTo(() => _api.SearchContractsAsync("ES", true, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>(
                [new ClientModels.Contract { Id = ContractKey, ActiveContract = true }]);

        await live.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        A.CallTo(() => _api.SearchContractsAsync("ES", false, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetBarsAsync_ShouldRequestTheConfiguredTier()
    {
        A.CallTo(() => _api.GetHistoricalBarsAsync(
                A<string>._, A<DateTime>._, A<DateTime>._, A<ClientModels.AggregateBarUnit>._,
                A<int>._, A<int>._, false, A<bool>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.AggregateBar>>([]);

        await _venue.GetBarsAsync(
            Contract,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        A.CallTo(() => _api.GetHistoricalBarsAsync(
                A<string>._, A<DateTime>._, A<DateTime>._, A<ClientModels.AggregateBarUnit>._,
                A<int>._, A<int>._, true, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Venue tagging is enforced, not assumed ---

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        OrderRequest foreign = MarketBuy() with
        {
            Contract = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey),
        };

        Func<Task> act = async () => await _venue.PlaceOrderAsync(foreign, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        // Especially dangerous on the flatten path: a colliding key would close the wrong position.
        VenueContractId foreign = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey);

        Func<Task> act = async () => await _venue.ClosePositionAsync(Account, foreign, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() => _api.ClosePositionAsync(A<int>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldAttachAStopLossBracket_FromTheProtectiveStop()
    {
        // The always-native safety stop (gh#11 inc 3): the protective stop rides the entry as a stop-loss
        // bracket, so the exchange holds it and attaches it on fill -- the position is never unprotected.
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        await _venue.PlaceOrderAsync(MarketBuy() with { ProtectiveStop = new Price(4_990m) }, CancellationToken.None);

        sent!.StopLossBracket.Should().NotBeNull();
        sent.StopLossBracket!.StopPrice.Should().Be(4_990m);
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldSendNoBracket_WhenThereIsNoProtectiveStop()
    {
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None); // no protective stop on this ticket

        sent!.StopLossBracket.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldForwardTheCustomTag_AndEchoItOnThePlacedOrder()
    {
        // gh#577 — the correlation handle. The client tag the source stamped rides the wire request, and the
        // acknowledgement carries it back, so a caller (a replay reconciling the transmit→journal window) can tie a
        // placed order to the source that placed it.
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        PlacedOrder placed = await _venue.PlaceOrderAsync(
            MarketBuy() with { CustomTag = "3f2504e0-4f89-41d3-9a0c-0305e82c3301" }, CancellationToken.None);

        sent!.CustomTag.Should().Be("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
        placed.CustomTag.Should().Be("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldSendNoCustomTag_WhenTheRequestCarriesNone()
    {
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        PlacedOrder placed = await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None); // operator path — no tag

        sent!.CustomTag.Should().BeNull();
        placed.CustomTag.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldAttachATakeProfitBracket_FromTheProfitTarget()
    {
        // The take-profit leg (gh#170): the target rides the entry as a limit-type take-profit bracket, so the
        // gateway holds it as the profit side of a native OCO alongside the stop-loss bracket.
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        await _venue.PlaceOrderAsync(MarketBuy() with { ProfitTarget = new Price(5_010m) }, CancellationToken.None);

        sent!.TakeProfitBracket.Should().NotBeNull();
        sent.TakeProfitBracket!.LimitPrice.Should().Be(5_010m); // a limit-type bracket at the target price
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldSendNoTakeProfitBracket_WhenThereIsNoProfitTarget()
    {
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest p, CancellationToken _) => sent = p)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None); // no take-profit on this ticket

        sent!.TakeProfitBracket.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldRefuseATrailingStop_WhileTheTicketCarriesNoTrailDistance()
    {
        OrderRequest trailing = MarketBuy() with { Type = OrderType.TrailingStop };

        Func<Task> act = async () => await _venue.PlaceOrderAsync(trailing, CancellationToken.None);

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Identity ---

    [Fact]
    public void AdapterLogicVersion_ShouldBePinned_SoSnapshotsStayInterpretable()
    {
        // ADR-0009 / gh#9: derived values (stage, mode) depend on adapter logic that CHANGES -- v1 was the
        // PRAC-only resolver (#86), v2 the roster-grounded families (#94). Snapshots stamp this version so a
        // past row stays interpretable after the logic moves on. Bump it when derivation behaviour changes;
        // this pin makes that bump a deliberate, reviewed act.
        _venue.AdapterLogicVersion.Should().Be(2);
    }

    // --- Accounts ---

    [Fact]
    public async Task GetAccountsAsync_ShouldReturnEveryAccountIncludingUnselectableOnes()
    {
        A.CallTo(() => _api.GetAccountsAsync(A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.TradingAccount>>(
        [
            new ClientModels.TradingAccount { Id = 9001, Name = "PRAC-50K", CanTrade = true, IsVisible = true, Simulated = true },
            new ClientModels.TradingAccount { Id = 9002, Name = "50KTC-V2", CanTrade = false, IsVisible = true, Simulated = false },
        ]);

        IReadOnlyList<VenueAccount> accounts = await _venue.GetAccountsAsync(CancellationToken.None);

        accounts.Should().HaveCount(2);
        accounts[1].IsSelectable.Should().BeFalse();
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldReportEveryAccountAsUndeclared_WhenTheFirmHasDeclaredNothing()
    {
        // With FirmConventions.None the adapter has no basis to say what is at stake -- and it must not guess
        // from the simulated flag (gh#60). Undeclared is refused everywhere, so the gap blocks trading rather
        // than hiding behind a plausible default. (_venue is built with None.)
        A.CallTo(() => _api.GetAccountsAsync(A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.TradingAccount>>(
        [
            new ClientModels.TradingAccount { Id = 9001, Name = "PRAC-50K", CanTrade = true, IsVisible = true, Simulated = true },
            new ClientModels.TradingAccount { Id = 9002, Name = "50KTC-V2", CanTrade = true, IsVisible = true, Simulated = false },
        ]);

        IReadOnlyList<VenueAccount> accounts = await _venue.GetAccountsAsync(CancellationToken.None);

        accounts.Should().OnlyContain(account => account.Mode == TradingMode.Undeclared);
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldResolveModeFromNameAndConventions_WhenTheFirmHasDeclared()
    {
        // The seam this increment builds (gh#76): stage from the name, meaning from the firm's conventions. A
        // PRAC account resolves to Practice; a Trading Combine name resolves to the EVALUATION stage -- but this
        // firm has not declared what Evaluation means, so the mode is still Undeclared: a resolved stage without
        // a declaration is never guessed into a tradeable mode.
        FirmConventions topstep = FirmConventions.For(
            "Topstep", (AccountStage.Practice, false), (AccountStage.Funded, true));
        ProjectXVenue venue = new(_api, _webSocket, ProjectXDataTier.Simulated, topstep);

        A.CallTo(() => _api.GetAccountsAsync(A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.TradingAccount>>(
        [
            new ClientModels.TradingAccount { Id = 9001, Name = "PRAC-50K-1234", CanTrade = true, IsVisible = true, Simulated = true },
            new ClientModels.TradingAccount { Id = 9002, Name = "50KTC-V2-DLL-0000", CanTrade = true, IsVisible = true, Simulated = true },
        ]);

        IReadOnlyList<VenueAccount> accounts = await venue.GetAccountsAsync(CancellationToken.None);

        accounts[0].Mode.Should().Be(TradingMode.Practice);   // PRAC -> Practice stage -> declared not-at-risk
        accounts[1].Stage.Should().Be(AccountStage.Evaluation); // 50KTC -> Trading Combine (gh#76 grounding)...
        accounts[1].Mode.Should().Be(TradingMode.Undeclared);   // ...but Evaluation is undeclared at this firm
    }

    // --- Contract resolution ---

    [Fact]
    public async Task ResolveContractAsync_ShouldPreferTheActiveContract_WhenSeveralMatch()
    {
        A.CallTo(() => _api.SearchContractsAsync("ES", A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Contract>>(
        [
            new ClientModels.Contract { Id = "CON.F.US.EP.H25", ActiveContract = false, TickSize = 0.25m, TickValue = 12.5m },
            new ClientModels.Contract { Id = ContractKey, ActiveContract = true, TickSize = 0.25m, TickValue = 12.5m },
        ]);

        ResolvedContract resolved = await _venue.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        resolved.Contract.Key.Should().Be(ContractKey);
        resolved.Instrument.Should().Be(InstrumentId.Parse("ES"));
        resolved.Contract.Venue.Should().Be(_venue.Id);
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldThrow_WhenNothingMatchesTheInstrument()
    {
        A.CallTo(() => _api.SearchContractsAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>([]);

        Func<Task> act = async () => await _venue.ResolveContractAsync(InstrumentId.Parse("ZZ"), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Positions ---

    [Fact]
    public async Task GetPositionsAsync_ShouldMapVenuePositions()
    {
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Position>>(
        [
            new ClientModels.Position
            {
                AccountId = 9001,
                ContractId = ContractKey,
                Type = ClientModels.PositionType.Short,
                Size = 2,
                AveragePrice = 5_000m,
            },
        ]);

        IReadOnlyList<PositionSnapshot> positions = await _venue.GetPositionsAsync(Account, CancellationToken.None);

        positions.Should().ContainSingle();
        positions[0].NetQuantity.Should().Be(-2);
    }

    // --- Placing orders: the gateway reports failure in the payload, not by throwing ---

    [Fact]
    public async Task PlaceOrderAsync_ShouldReturnThePlacedOrder_WhenTheGatewayAccepts()
    {
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        PlacedOrder placed = await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        placed.VenueOrderId.Should().Be("555");
        placed.Account.Should().Be(Account);
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheGatewayReportsFailure()
    {
        // A rejected order comes back as Success=false with a 200 response. Ignoring that would leave us
        // believing a position exists that does not.
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = false, ErrorCode = 42, ErrorMessage = "Insufficient margin" });

        Func<Task> act = async () => await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>().Where(e => e.Message.Contains("Insufficient margin"));
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheGatewayReportsSuccessWithoutAnOrderId()
    {
        // Success with no handle leaves nothing to cancel or flatten against -- refuse rather than carry on.
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = null });

        Func<Task> act = async () => await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldTranslateTheTicketToTheGatewaysVocabulary()
    {
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest r, CancellationToken _) => sent = r)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 1 });

        await _venue.PlaceOrderAsync(MarketBuy(quantity: 3), CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.AccountId.Should().Be(9001);
        sent.ContractId.Should().Be(ContractKey);
        sent.Side.Should().Be(ClientModels.OrderSide.Bid);
        sent.Type.Should().Be(ClientModels.OrderType.Market);
        sent.Size.Should().Be(3);
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldThrow_WhenTheGatewayReportsFailure()
    {
        A.CallTo(() => _api.CancelOrderAsync(9001, 555, A<CancellationToken>._))
            .Returns(new ClientModels.CancelOrderResponse { Success = false, ErrorMessage = "Unknown order" });

        Func<Task> act = async () => await _venue.CancelOrderAsync(Account, "555", CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Modifying a working order in place (gh#259) ---

    [Fact]
    public async Task ModifyOrderAsync_ShouldThrow_WhenTheGatewayReportsFailure()
    {
        // The same reported-failure-without-throwing hazard as cancel: a false Success must not be read as done.
        A.CallTo(() => _api.ModifyOrderAsync(A<ClientModels.ModifyOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.ModifyOrderResponse { Success = false, ErrorMessage = "Order already filled" });

        Func<Task> act = async () =>
            await _venue.ModifyOrderAsync(Account, "555", new Price(5_000m), stopPrice: null, size: 2, CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    [Fact]
    public async Task ModifyOrderAsync_ShouldCallTheGatewayWithMappedIdsAndTheChangedFields()
    {
        // Ids go through the venue-qualifier guards (never parsed inline); only the fields the caller passes are
        // sent, and a null leaves the gateway's current value untouched.
        ClientModels.ModifyOrderRequest? sent = null;
        A.CallTo(() => _api.ModifyOrderAsync(A<ClientModels.ModifyOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.ModifyOrderRequest r, CancellationToken _) => sent = r)
            .Returns(new ClientModels.ModifyOrderResponse { Success = true });

        await _venue.ModifyOrderAsync(Account, "555", new Price(5_000m), stopPrice: null, size: 2, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.AccountId.Should().Be(9001);
        sent.OrderId.Should().Be(555);
        sent.LimitPrice.Should().Be(5_000m);
        sent.StopPrice.Should().BeNull();
        sent.Size.Should().Be(2);
    }

    // --- Closing a position: the venue's own view is the answer (ADR-0013) ---

    [Fact]
    public async Task ClosePositionAsync_ShouldReportFlat_WhenTheVenueNoLongerHoldsThePosition()
    {
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = true });
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Position>>([]);

        PositionSnapshot result = await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        result.IsFlat.Should().BeTrue();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldReportWhatRemains_WhenTheVenueStillHoldsThePosition()
    {
        // Never assume a close worked. If size survives, say so -- auto-flatten depends on this being honest.
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = true });
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Position>>(
        [
            new ClientModels.Position
            {
                AccountId = 9001,
                ContractId = ContractKey,
                Type = ClientModels.PositionType.Long,
                Size = 1,
                AveragePrice = 5_000m,
            },
        ]);

        PositionSnapshot result = await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        result.IsFlat.Should().BeFalse();
        result.NetQuantity.Should().Be(1);
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldThrow_WhenTheGatewayRefusesTheClose()
    {
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = false, ErrorMessage = "Market closed" });

        Func<Task> act = async () => await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Bars ---

    [Fact]
    public async Task GetBarsAsync_ShouldMapTheGatewaysBars()
    {
        A.CallTo(() => _api.GetHistoricalBarsAsync(
                ContractKey,
                A<DateTime>._,
                A<DateTime>._,
                ClientModels.AggregateBarUnit.Minute,
                5,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.AggregateBar>>(
            [
                new ClientModels.AggregateBar { Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc), Open = 5_000m, High = 5_010m, Low = 4_995m, Close = 5_005m, Volume = 900 },
            ]);

        IReadOnlyList<Bar> bars = await _venue.GetBarsAsync(
            Contract,
            new DateTimeOffset(2026, 7, 20, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(new Price(5_005m));
    }

    // --- Streaming: two SignalR hubs behind an IAsyncEnumerable ---

    [Fact]
    public void StreamQuotesAsync_ShouldNotSubscribe_WhenTheSequenceIsNeverEnumerated()
    {
        // Registration lives inside the iterator, so an unconsumed stream leaves no handler or subscription
        // behind to accumulate ticks for the life of the process.
        _ = _venue.StreamQuotesAsync(Contract, CancellationToken.None);

        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void StreamQuotesAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        VenueContractId foreign = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey);

        Action act = () => _venue.StreamQuotesAsync(foreign, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldYieldMappedQuotes_WhenTheMarketHubPublishes()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = _venue.StreamQuotesAsync(Contract, cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();

        // The handler is attached before the subscribe call, so once that has run a published tick is captured.
        await subscribed.Task;

        // The gateway tags quotes with the PRODUCT ROOT (F.US.EP), not the full contract id we subscribed with
        // (CON.F.US.EP.M25) -- the gh#163 defect: a filter on the full id dropped all 533 live ticks. Feed what
        // the venue actually sends, not what makes the filter pass.
        _webSocket.PriceUpdateReceived += Raise.With(new ClientModels.PriceUpdate
        {
            Symbol = "F.US.EP",
            BestBid = 5_000m,
            BestAsk = 5_000.25m,
            Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc),
        });

        (await next).Should().BeTrue();
        quotes.Current.Bid.Should().Be(new Price(5_000m));
        quotes.Current.Ask.Should().Be(new Price(5_000.25m));
        quotes.Current.BidSize.Should().BeNull();

        await quotes.DisposeAsync();
        A.CallTo(() => _webSocket.UnsubscribeFromPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldIgnoreUpdates_WhenTheyAreForAnotherContract()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = _venue.StreamQuotesAsync(Contract, cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();
        await subscribed.Task;

        // One hub carries every subscribed contract, so another product's root must not leak into this stream;
        // both are matched on the product root (gh#163), never the full contract id.
        _webSocket.PriceUpdateReceived += Raise.With(
            new ClientModels.PriceUpdate { Symbol = "F.US.ENQ", BestBid = 1m, BestAsk = 2m });
        _webSocket.PriceUpdateReceived += Raise.With(
            new ClientModels.PriceUpdate { Symbol = "F.US.EP", BestBid = 5_000m, BestAsk = 5_000.25m });

        (await next).Should().BeTrue();
        quotes.Current.Bid.Should().Be(new Price(5_000m));

        await quotes.DisposeAsync();
    }

    // gh#631 — the fill-history read. This is the ONE method the stranded-reconcile veto depends on: if its tag match
    // or its FillVolume predicate is wrong, every answer silently becomes NoFillFound, the veto never fires, and the
    // second-entry hazard it exists to close is exactly as open as before — with the whole suite green. So the
    // translation is pinned here rather than only through the endpoints that consume it.
    private static ClientModels.Order HistoryOrder(
        long id, string? tag, int fillVolume, decimal? filledPrice = null, DateTime? created = null) =>
        new()
        {
            Id = id,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            CustomTag = tag,
            FillVolume = fillVolume,
            FilledPrice = filledPrice,
            CreationTimestamp = created ?? new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldReportFilled_WhenHistoryCarriesTheTagWithExecutedVolume()
    {
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Order>>(
                [HistoryOrder(1, "someone-else", 3), HistoryOrder(77, "the-tag", 2, 5301.25m)]);

        TaggedFillEvidence evidence = await _venue.FindFilledOrderByTagAsync(
            Account, "the-tag", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        evidence.Status.Should().Be(TaggedFillStatus.Filled);
        evidence.VetoesRelease.Should().BeTrue("a reported fill is what stops a stranded row being released");
        evidence.FilledSize.Should().Be(2m);
        evidence.FilledPrice.Should().Be(5301.25m);
        evidence.VenueOrderKey.Should().Be("77");
    }

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldReportNoFillFound_WhenNoRecordCarriesTheTag()
    {
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Order>>([HistoryOrder(1, "someone-else", 3)]);

        TaggedFillEvidence evidence = await _venue.FindFilledOrderByTagAsync(
            Account, "the-tag", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        evidence.Status.Should().Be(TaggedFillStatus.NoFillFound);
        evidence.VetoesRelease.Should().BeFalse();
    }

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldReportNoFillFound_WhenTheTaggedOrderNeverExecuted()
    {
        // The tag is present but nothing executed — a placed-then-cancelled or rejected order. That must NOT veto:
        // the row genuinely never entered a position, so releasing it is correct.
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Order>>([HistoryOrder(77, "the-tag", fillVolume: 0)]);

        TaggedFillEvidence evidence = await _venue.FindFilledOrderByTagAsync(
            Account, "the-tag", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        evidence.Status.Should().Be(TaggedFillStatus.NoFillFound);
    }

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldTakeTheEarliestExecution_WhenOneTagHasTwo()
    {
        // The tag is a CORRELATION handle, not a venue idempotency key, so a re-attempt after a transport fault can
        // leave two executed records under it. The gateway documents no ordering, so the pick must be deterministic:
        // the earliest execution is the one that actually opened the position.
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Order>>(
            [
                HistoryOrder(99, "the-tag", 1, 5310m, new DateTime(2026, 8, 3, 12, 5, 0, DateTimeKind.Utc)),
                HistoryOrder(77, "the-tag", 2, 5301m, new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)),
            ]);

        TaggedFillEvidence evidence = await _venue.FindFilledOrderByTagAsync(
            Account, "the-tag", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        evidence.VenueOrderKey.Should().Be("77", "the earliest execution under the tag is the entry that opened the position");
    }

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldSearchFromTheCallersInstant_WithAnOpenEndedWindow()
    {
        // The window is the caller's safety margin: it starts at the stranded row's own instant so it always covers
        // the transmit, and stays open-ended because an order can fill long after it was placed. A window that starts
        // too late would miss the fill and let the release proceed.
        DateTime? capturedStart = null;
        DateTime? capturedEnd = null;
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .Invokes((int _, DateTime? start, DateTime? end, CancellationToken _) =>
            {
                capturedStart = start;
                capturedEnd = end;
            })
            .Returns<IEnumerable<ClientModels.Order>>([]);

        DateTimeOffset since = new(2026, 8, 3, 11, 30, 0, TimeSpan.FromHours(-5));
        await _venue.FindFilledOrderByTagAsync(Account, "the-tag", since, CancellationToken.None);

        capturedStart.Should().Be(since.UtcDateTime, "the search starts at the caller's instant, normalised to UTC");
        capturedEnd.Should().BeNull("an order can fill well after it was placed, so the window stays open-ended");
    }

    [Fact]
    public async Task FindFilledOrderByTagAsync_ShouldLetAGatewayFailureSurface_SoTheCallerReportsUnavailable()
    {
        // It must NOT swallow the failure into NoFillFound. The calling service maps a throw to Unavailable, which
        // strands the row for an operator — "we could not tell" is never "it did not fill".
        A.CallTo(() => _api.GetOrdersAsync(9001, A<DateTime?>._, A<DateTime?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("gateway order-search failed"));

        Func<Task> act = async () => await _venue.FindFilledOrderByTagAsync(
            Account, "the-tag", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
